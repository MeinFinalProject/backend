#include "security/device_credential.hpp"
#include "storage/outbox_repository.hpp"
#include "sync/device_channel.hpp"
#include <atomic>
#include <cstdlib>
#include <iostream>
#include <syncstream>
#include <thread>
#include <winrt/base.h>

// Controlled operational telemetry only: no camera, recognition or biometric claims.
// Credentials arrive in a private child environment, never command-line arguments.
int wmain(int argc, wchar_t **argv)
{
    try
    {
        if (argc != 4) return 2;
        winrt::init_apartment(winrt::apartment_type::multi_threaded);
        auto root = std::filesystem::path(argv[3]);
        edge_app::Database db(root / "realtime.db");
        edge_app::OutboxRepository outbox(db);
        char *raw = nullptr;
        std::size_t size = 0;
        if (_dupenv_s(&raw, &size, "TA_TEST_DEVICE_TOKEN") || !raw) return 2;
        std::string token(raw);
        free(raw);
        _putenv_s("TA_TEST_DEVICE_TOKEN", "");
        edge_app::protect_token(root / "device.dpapi", token);
        token.clear();
        std::unique_ptr<edge_app::DeviceChannel> channel;
        std::string version, runtime = "running";
        auto connect = [&] {
            channel = std::make_unique<edge_app::DeviceChannel>(winrt::to_string(argv[2]), false, root / "device.dpapi",
                winrt::to_string(argv[1]), outbox, [] { std::osyncstream(std::cout) << "RECONCILE\n" << std::flush; });
            channel->runtime_state(runtime);
            channel->gallery_installed(version, version.empty() ? 0 : 1);
            channel->frame_processed();
        };
        connect();
        std::string command;
        while (std::getline(std::cin, command))
        {
            if (command == "stop") break;
            if (command == "disconnect") channel.reset();
            else if (command == "connect") connect();
            else if (command == "frame" && channel) channel->frame_processed();
            else if (command.starts_with("install "))
            {
                version = command.substr(8);
                if (channel) channel->gallery_installed(version, 1);
            }
            else if (command.starts_with("state "))
            {
                runtime = command.substr(6);
                if (channel) channel->runtime_state(runtime);
            }
        }
        channel.reset();
        std::cout << "STOPPED\n" << std::flush;
        return 0;
    }
    catch (...) { std::cerr << "Native realtime probe failed; sensitive diagnostics suppressed\n"; return 1; }
}
