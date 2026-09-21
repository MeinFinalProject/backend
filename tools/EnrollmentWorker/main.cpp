#include "scrfd/scrfd.hpp"
#include "arcface/arcface.hpp"
#include <wincodec.h>
#include <fcntl.h>
#include <io.h>
#include <algorithm>
#include <cmath>
#include <cstdio>
#include <iomanip>
#include <iostream>
#include <iterator>
#include <vector>

namespace {
int reject(char const* code) { std::cout << "{\"error\":\"" << code << "\"}"; return 0; }
struct Image { UINT width{}, height{}; std::vector<unsigned char> rgb; };
Image decode(std::vector<unsigned char>& encoded) {
    winrt::com_ptr<IWICImagingFactory> factory;
    winrt::check_hresult(CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(factory.put())));
    winrt::com_ptr<IWICStream> stream;
    winrt::check_hresult(factory->CreateStream(stream.put()));
    winrt::check_hresult(stream->InitializeFromMemory(encoded.data(), static_cast<DWORD>(encoded.size())));
    winrt::com_ptr<IWICBitmapDecoder> decoder;
    winrt::check_hresult(factory->CreateDecoderFromStream(stream.get(), nullptr, WICDecodeMetadataCacheOnDemand, decoder.put()));
    GUID container{};
    winrt::check_hresult(decoder->GetContainerFormat(&container));
    if (container != GUID_ContainerFormatJpeg && container != GUID_ContainerFormatPng) throw std::runtime_error("format");
    UINT count{};
    winrt::check_hresult(decoder->GetFrameCount(&count));
    if (count != 1) throw std::runtime_error("frames");
    winrt::com_ptr<IWICBitmapFrameDecode> frame;
    winrt::check_hresult(decoder->GetFrame(0, frame.put()));
    Image image;
    winrt::check_hresult(frame->GetSize(&image.width, &image.height));
    if (image.width < 112 || image.height < 112 || image.width > 2048 || image.height > 2048) throw std::runtime_error("size");
    winrt::com_ptr<IWICFormatConverter> converter;
    winrt::check_hresult(factory->CreateFormatConverter(converter.put()));
    winrt::check_hresult(converter->Initialize(frame.get(), GUID_WICPixelFormat24bppRGB, WICBitmapDitherTypeNone, nullptr, 0, WICBitmapPaletteTypeCustom));
    image.rgb.resize(static_cast<size_t>(image.width) * image.height * 3);
    winrt::check_hresult(converter->CopyPixels(nullptr, image.width * 3, static_cast<UINT>(image.rgb.size()), image.rgb.data()));
    return image;
}

// Only this image-to-camera adapter is new. Edge owns detection, five-point
// alignment, GPU tensorization, WinML/DirectML inference and L2 normalization.
vision_runtime::GpuFrame upload(Image const& image) {
    UINT width = (image.width + 1) & ~1u, height = (image.height + 1) & ~1u;
    std::vector<unsigned char> nv12(static_cast<size_t>(width) * height * 3 / 2);
    auto pixel = [&](UINT x, UINT y, int channel) { return image.rgb[(static_cast<size_t>(std::min(y, image.height - 1)) * image.width + std::min(x, image.width - 1)) * 3 + channel] / 255.0; };
    auto byte = [](double value) { return static_cast<unsigned char>(std::clamp(std::round(value), 0.0, 255.0)); };
    for (UINT y = 0; y < height; y += 2) for (UINT x = 0; x < width; x += 2) {
        double cb = 0, cr = 0;
        for (UINT dy = 0; dy < 2; ++dy) for (UINT dx = 0; dx < 2; ++dx) {
            double r = pixel(x + dx, y + dy, 0), g = pixel(x + dx, y + dy, 1), b = pixel(x + dx, y + dy, 2);
            double luma = .2126 * r + .7152 * g + .0722 * b;
            nv12[static_cast<size_t>(y + dy) * width + x + dx] = byte(16 + 219 * luma);
            cb += (b - luma) / 1.8556; cr += (r - luma) / 1.5748;
        }
        auto offset = static_cast<size_t>(width) * height + static_cast<size_t>(y / 2) * width + x;
        nv12[offset] = byte(128 + 224 * cb / 4); nv12[offset + 1] = byte(128 + 224 * cr / 4);
    }
    winrt::com_ptr<ID3D11Device> device;
    winrt::com_ptr<ID3D11DeviceContext> context;
    D3D_FEATURE_LEVEL feature{};
    winrt::check_hresult(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT,
        nullptr, 0, D3D11_SDK_VERSION, device.put(), &feature, context.put()));
    D3D11_TEXTURE2D_DESC description{};
    description.Width = width; description.Height = height; description.MipLevels = 1; description.ArraySize = 1;
    description.Format = DXGI_FORMAT_NV12; description.SampleDesc.Count = 1; description.Usage = D3D11_USAGE_DEFAULT;
    description.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    D3D11_SUBRESOURCE_DATA initial{nv12.data(), width, static_cast<UINT>(nv12.size())};
    vision_runtime::GpuFrame result;
    winrt::check_hresult(device->CreateTexture2D(&description, &initial, result.texture.put()));
    result.width = width; result.height = height; result.dxgi_format = DXGI_FORMAT_NV12; result.frame_id = 1;
    return result;
}
}

int wmain(int argc, wchar_t** argv) {
    if (argc != 3) return 2;
    try {
        winrt::init_apartment(winrt::apartment_type::multi_threaded);
        _setmode(_fileno(stdin), _O_BINARY);
        std::vector<unsigned char> encoded;
        char block[8192];
        while (std::cin.read(block, sizeof(block)) || std::cin.gcount()) {
            encoded.insert(encoded.end(), block, block + std::cin.gcount());
            if (encoded.size() > 5 * 1024 * 1024) return reject("invalid_image");
        }
        Image image;
        try { image = decode(encoded); }
        catch (winrt::hresult_error const& error) { std::cerr << "decode_hresult=" << std::hex << error.code().value; return reject("invalid_image"); }
        catch (...) { return reject("invalid_image"); }
        auto frame = upload(image);
        vision_runtime::ScrfdDetector detector({argv[1]});
        auto faces = detector.detect(frame);
        if (faces.size() != 1) return reject("exactly_one_face_required");
        auto const& face = faces.front();
        // SCRFD already applies the Edge default score threshold (0.50).
        if (face.x2 - face.x1 < 80 || face.y2 - face.y1 < 80) {
            std::cerr << "quality: score=" << face.score << " width=" << face.x2 - face.x1 << " height=" << face.y2 - face.y1;
            return reject("face_quality_insufficient");
        }
        vision_runtime::arcface::ArcFaceRecognizer recognizer({argv[2]});
        auto result = recognizer.evaluate(frame, face);
        if (!std::isfinite(result.transform.rmse) || result.transform.rmse > 8) {
            std::cerr << "quality: alignment_error=" << result.transform.rmse;
            return reject("face_quality_insufficient");
        }
        std::cout << std::setprecision(9) << "{\"detection_score\":" << face.score << ",\"alignment_error\":" << result.transform.rmse << ",\"embedding\":[";
        for (size_t i = 0; i < result.embedding.size(); ++i) { if (i) std::cout << ','; std::cout << result.embedding[i]; }
        std::cout << "]}";
        return 0;
    } catch (...) { return 3; }
}
