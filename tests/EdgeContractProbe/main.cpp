#include "domain/uuid_v7.hpp"
#include "security/device_credential.hpp"
#include "storage/gallery_repository.hpp"
#include "storage/outbox_repository.hpp"
#include "sync/attendance_sync.hpp"
#include "sync/gallery_sync.hpp"
#include "sync/wire_format.hpp"
#include <algorithm>
#include <iostream>
#include <limits>
#include <stdexcept>
#include <winrt/base.h>

namespace
{
void require(bool condition)
{
    if (!condition)
        throw std::runtime_error("Edge integration assertion failed");
}

// Fault injection stays in this test. Successful calls use the real WinRT HTTPS transport.
class InterruptedTransport final : public edge_app::Transport
{
  public:
    explicit InterruptedTransport(edge_app::Transport &inner) : inner_(inner) {}
    bool offline = false, lose_ack = false;
    edge_app::HttpResponse last;
    void set_token(std::string const &token) override { inner_.set_token(token); }
    edge_app::HttpResponse request(std::string const &method, std::string const &path,
                                  std::string const &body, std::string const &etag) override
    {
        if (offline)
            throw std::runtime_error("Simulated offline connection");
        last = inner_.request(method, path, body, etag);
        if (lose_ack && method == "POST")
        {
            lose_ack = false;
            throw std::runtime_error("Simulated lost acknowledgement after server response");
        }
        return last;
    }
  private:
    edge_app::Transport &inner_;
};
} // namespace

int wmain(int argc, wchar_t **argv)
{
    char const *stage = "arguments";
    try
    {
        if (argc != 6)
            throw std::runtime_error("Expected credential-file, device-id, model-sha256, base-url, scratch-directory");
        winrt::init_apartment(winrt::apartment_type::multi_threaded);
        auto const device = winrt::to_string(argv[2]);
        auto const hash = winrt::to_string(argv[3]);
        auto transport = edge_app::http_transport(winrt::to_string(argv[4]));
        stage = "invalid credential rejection";
        transport->set_token("invalid-integration-test-token");
        require(transport->request("GET", "/gallery", "", "").status == 401);
        stage = "DPAPI credential";
        transport->set_token(edge_app::unprotect_token(argv[1]));
        InterruptedTransport http(*transport);
        auto const db_file = std::filesystem::path(argv[5]) / "probe.db";
        require(!std::filesystem::exists(db_file));
        edge_app::AttendanceRecord row;
        std::string version;
        stage = "gallery download and persistence";
        {
            edge_app::Database database(db_file);
            edge_app::GalleryRepository gallery(database);
            edge_app::GallerySync download(gallery, http, hash);
            auto const now = edge_app::utc_ms();
            require(download.step(now) == edge_app::SyncResult::Continue);
            require(http.last.status == 200 && download.take_dirty());
            version = gallery.load().version;
            require(!version.empty());
            stage = "conditional gallery download";
            require(download.step(edge_app::utc_ms() + 60001) == edge_app::SyncResult::Continue);
            require(http.last.status == 304 && !download.take_dirty());
            std::cout << "PASS: HTTPS, rejected invalid credential, DPAPI, durable gallery, ETag 304\n";

            edge_app::AttendanceObservation observation;
            stage = "offline enqueue and retry";
            observation.occurred_at_utc_ms = edge_app::utc_ms();
            observation.track_id = UINT64_MAX;
            observation.camera_frame_id = 9007199254740993ULL;
            observation.identity_id = "backend-contract-probe";
            observation.similarity = .75f;
            observation.similarity_margin = .1f;
            observation.pad_median_p_real = .99f;
            row = edge_app::envelope(observation, device, version);
            edge_app::OutboxRepository outbox(database);
            outbox.enqueue(row);
            edge_app::AttendanceSync upload(outbox, http, device);
            http.offline = true;
            require(upload.step(edge_app::utc_ms()) == edge_app::SyncResult::Continue);
            require(outbox.pending_count() == 1 && outbox.dead_count() == 0);
        }
        {
            // Reopen the actual SQLite file; retry metadata and original event must survive.
            stage = "offline recovery and lost acknowledgement";
            edge_app::Database database(db_file);
            edge_app::GalleryRepository gallery(database);
            require(gallery.load().version == version);
            edge_app::OutboxRepository outbox(database);
            auto pending = outbox.due(std::numeric_limits<std::int64_t>::max());
            require(pending.size() == 1 && pending[0].id == row.id &&
                    pending[0].payload == row.payload && pending[0].attempts == 1);
            require(outbox.due(pending[0].next_attempt_ms - 1).empty());
            http.offline = false;
            http.lose_ack = true;
            edge_app::AttendanceSync upload(outbox, http, device);
            upload.step(std::max(edge_app::utc_ms(), pending[0].next_attempt_ms));
            require(http.last.status == 200);
            auto receipts = edge_app::parse_receipts(http.last.body, {row});
            require(receipts.size() == 1 && receipts[0].disposition == "accepted");
            require(outbox.pending_count() == 1 && outbox.dead_count() == 0);
            std::cout << "PASS: offline queue/reopen, persisted retry delay, backend commit with lost ACK\n";
        }
        {
            stage = "duplicate delivery and durable acknowledgement";
            edge_app::Database database(db_file);
            edge_app::OutboxRepository outbox(database);
            auto pending = outbox.due(std::numeric_limits<std::int64_t>::max());
            require(pending.size() == 1 && pending[0].payload == row.payload && pending[0].attempts == 2);
            edge_app::AttendanceSync upload(outbox, http, device);
            upload.step(std::max(edge_app::utc_ms(), pending[0].next_attempt_ms));
            require(http.last.status == 200);
            auto receipts = edge_app::parse_receipts(http.last.body, {row});
            require(receipts.size() == 1 && receipts[0].disposition == "duplicate");
            require(outbox.pending_count() == 0 && outbox.dead_count() == 0);
        }
        {
            edge_app::Database database(db_file);
            edge_app::OutboxRepository outbox(database);
            require(outbox.pending_count() == 0 && outbox.dead_count() == 0);
        }
        std::cout << "PASS: duplicate retry after reopen, durable local acknowledgement, stable 64-bit identifiers\n";
        std::cout << "Synthetic raw event retained by backend: " << row.id << "\n";
        return 0;
    }
    catch (...)
    {
        std::cerr << "FAIL: " << stage << " (sensitive exception details suppressed)\n";
        return 1;
    }
}
