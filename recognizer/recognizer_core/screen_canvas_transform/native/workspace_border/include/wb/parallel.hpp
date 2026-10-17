#pragma once

#include <algorithm>
#include <thread>
#include <vector>

namespace wb {

// Calls fn(begin, end) on contiguous bands of [0, rows), one band per thread.
// Every row is computed by exactly one band with the same per-row arithmetic,
// so the result never depends on the band count. fn must not throw.
template <class Fn>
void ParallelRows(int rows, const Fn& fn) {
  constexpr int kMaxBands = 8;
  constexpr int kMinRowsPerBand = 64;
  const int hardware = static_cast<int>(std::thread::hardware_concurrency());
  const int bands = std::max(1, std::min({kMaxBands, hardware, rows / kMinRowsPerBand}));
  if (bands <= 1) {
    fn(0, rows);
    return;
  }
  std::vector<std::thread> threads;
  threads.reserve(bands - 1);
  for (int i = 1; i < bands; ++i)
    threads.emplace_back([&fn, rows, bands, i] { fn(rows * i / bands, rows * (i + 1) / bands); });
  fn(0, rows / bands);
  for (auto& thread : threads) thread.join();
}

}  // namespace wb
