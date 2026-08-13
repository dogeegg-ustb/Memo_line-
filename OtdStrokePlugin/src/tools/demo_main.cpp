#include "otd_stroke/c_api.h"
#include "otd_stroke/types.hpp"
#include "recorder/stroke_recorder.hpp"

#include <chrono>
#include <filesystem>
#include <iostream>
#include <thread>

namespace {

std::uint64_t nowMs() {
    using namespace std::chrono;
    return duration_cast<milliseconds>(system_clock::now().time_since_epoch()).count();
}

void drawStroke(otd_stroke::StrokeRecorder& recorder, std::uint64_t strokeIndex, std::uint64_t& seq) {
    const auto t0 = nowMs();
    recorder.onPenDown();
    for (int i = 0; i < 5; ++i) {
        otd_stroke::SamplePoint p;
        p.timestampMs = t0 + static_cast<std::uint64_t>(i) * 4;
        p.x = 100.0 + static_cast<double>(strokeIndex) * 10.0 + i;
        p.y = 200.0 + i * 2.0;
        p.pressure = 0.2 + i * 0.1;
        p.inContact = true;
        p.tiltX = 1.5;
        p.tiltY = -0.5;
        p.sequenceId = ++seq;
        recorder.onPoint(p);
    }
    recorder.onPenUp();
}

}  // namespace

int main(int argc, char** argv) {
    const std::filesystem::path root =
        argc > 1 ? std::filesystem::path(argv[1]) : std::filesystem::current_path();

    otd_stroke::StrokeRecorderConfig config;
    config.otdRoot = root;
    config.device.name = "DemoTablet";
    config.device.id = "demo-001";
    config.penUpTimeoutMs = 500;
    config.maxStrokesPerSegment = 100;

    otd_stroke::StrokeRecorder recorder(config);
    recorder.startSession();
    std::cout << "session file: " << recorder.currentFilePath().string() << '\n';

    std::uint64_t seq = 0;
    // Two strokes with short gap (<500ms) => same segment when flushed by timeout.
    drawStroke(recorder, 1, seq);
    std::this_thread::sleep_for(std::chrono::milliseconds(80));
    drawStroke(recorder, 2, seq);

    // Wait for pen-up timeout flush.
    const auto deadline = nowMs() + 700;
    while (nowMs() < deadline) {
        recorder.tick(nowMs());
        std::this_thread::sleep_for(std::chrono::milliseconds(20));
    }

    recorder.stopSession();
    const auto stats = recorder.queueStats();
    std::cout << "segments in memory: " << recorder.session().segments.size() << '\n';
    std::cout << "queue written: " << stats.written << " droppedOldest: " << stats.droppedOldest
              << '\n';
    std::cout << "stroke dir: " << (root / "stroke").string() << '\n';
    return 0;
}
