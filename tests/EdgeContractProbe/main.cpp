#include "domain/uuid_v7.hpp"
#include "security/device_credential.hpp"
#include "sync/http_client.hpp"
#include "sync/wire_format.hpp"
#include <winrt/base.h>
#include <iostream>
#include <stdexcept>

void require(bool condition)
{
    if (!condition) throw std::runtime_error("Edge contract assertion failed");
}

int main(int argc, char **argv)
{
    try
    {
        if (argc != 4) throw std::runtime_error("Expected credential-file, device-id, model-sha256");
        winrt::init_apartment(winrt::apartment_type::multi_threaded);
        auto transport = edge_app::http_transport("https://localhost:7143/api/v1");
        transport->set_token(edge_app::unprotect_token(argv[1]));
        auto response = transport->request("GET", "/gallery", "", "");
        require(response.status == 200);
        const auto gallery = edge_app::parse_gallery(response.body, response.etag, argv[3]);
        auto cached = transport->request("GET", "/gallery", "", gallery.etag);
        require(cached.status == 304);
        edge_app::AttendanceObservation observation;
        observation.occurred_at_utc_ms = edge_app::utc_ms();
        observation.track_id = UINT64_MAX;
        observation.camera_frame_id = 9007199254740993ULL;
        observation.identity_id = "backend-contract-probe";
        observation.similarity = .75f;
        observation.similarity_margin = .1f;
        observation.pad_median_p_real = .99f;
        auto row = edge_app::envelope(observation, argv[2], gallery.version);
        auto payload = edge_app::batch_payload(argv[2], {row});
        auto accepted = transport->request("POST", "/attendance-events/batch", payload, "");
        require(accepted.status == 200);
        auto receipts = edge_app::parse_receipts(accepted.body, {row});
        require(receipts.size() == 1 && receipts[0].disposition == "accepted");
        auto duplicate = transport->request("POST", "/attendance-events/batch", payload, "");
        require(duplicate.status == 200);
        receipts = edge_app::parse_receipts(duplicate.body, {row});
        require(receipts.size() == 1 && receipts[0].disposition == "duplicate");
        std::cout << "PASS: Edge WinRT HTTPS, DPAPI credential, gallery parser, ETag 304, attendance accepted/duplicate ACKs\n";
        return 0;
    }
    catch (...)
    {
        std::cerr << "FAIL: native Edge contract probe (sensitive exception details suppressed)\n";
        return 1;
    }
}
