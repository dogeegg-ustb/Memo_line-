#include "exporter/json_exporter.hpp"
#include "otd_stroke/types.hpp"
#include "reader/binary_reader.hpp"
#include "recorder/stroke_recorder.hpp"

#include <chrono>
#include <filesystem>
#include <iostream>
#include <thread>

namespace fs = std::filesystem;

namespace {

std::uint64_t nowMs() {
    using namespace std::chrono;
    return duration_cast<milliseconds>(system_clock::now().time_since_epoch()).count();
}

void fail(const char* msg) {
    std::cerr << "FAIL: " << msg << '\n';
    std::exit(1);
}

void addStroke(otd_stroke::StrokeRecorder& r, std::uint64_t& seq, double x0) {
    const auto t0 = nowMs();
    r.onPenDown();
    for (int i = 0; i < 3; ++i) {
        otd_stroke::SamplePoint p;
        p.timestampMs = t0 + static_cast<std::uint64_t>(i);
        p.x = x0 + i;
        p.y = 10.0 + i;
        p.pressure = 0.5;
        p.inContact = true;
        p.sequenceId = ++seq;
        r.onPoint(p);
    }
    r.onPenUp();
}

}  // namespace

int main() {
    const auto root = fs::temp_directory_path() / "otd_stroke_test";
    fs::remove_all(root);
    fs::create_directories(root);

    otd_stroke::StrokeRecorderConfig config;
    config.otdRoot = root;
    config.device.name = "TestPad";
    config.device.id = "t-1";
    config.penUpTimeoutMs = 80;
    config.maxStrokesPerSegment = 3;

    otd_stroke::StrokeRecorder recorder(config);
    recorder.startSession();
    if (recorder.state() != otd_stroke::RecorderState::Idle) {
        fail("expected Idle after start");
    }

    std::uint64_t seq = 0;
    addStroke(recorder, seq, 1.0);
    addStroke(recorder, seq, 2.0);
    addStroke(recorder, seq, 3.0);

    // Threshold flush should have fired on 3rd pen-up.
    if (recorder.session().segments.empty()) {
        fail("expected threshold flush segment");
    }
    if (recorder.session().segments.back().reason != otd_stroke::FlushReason::StrokeCountThreshold) {
        fail("expected StrokeCountThreshold");
    }

    addStroke(recorder, seq, 4.0);
    const auto waitUntil = nowMs() + 200;
    while (nowMs() < waitUntil) {
        recorder.tick(nowMs());
        std::this_thread::sleep_for(std::chrono::milliseconds(10));
    }
    if (recorder.session().segments.size() < 2) {
        fail("expected timeout flush segment");
    }
    if (recorder.session().segments.back().reason != otd_stroke::FlushReason::PenUpTimeout) {
        fail("expected PenUpTimeout");
    }

    const auto binPath = recorder.currentFilePath();
    recorder.stopSession();

    if (!fs::exists(binPath)) {
        fail("binary file missing");
    }

    otd_stroke::StrokeBinaryReader reader;
    const auto loaded = reader.read(binPath.string());
    if (loaded.segments.size() < 2) {
        fail("reader segment count");
    }
    if (loaded.segments[0].strokes.size() != 3) {
        fail("first segment stroke count");
    }
    if (loaded.segments[0].strokes[0].points.size() != 3) {
        fail("point count");
    }

    otd_stroke::StrokeJsonExporter exporter;
    const auto json = exporter.exportSession(loaded);
    if (json.find("\"PenUpTimeout\"") == std::string::npos) {
        fail("json missing PenUpTimeout");
    }
    if (json.find("\"pressure\"") == std::string::npos) {
        fail("json missing pressure");
    }

    std::cout << "ok\n";
    return 0;
}
