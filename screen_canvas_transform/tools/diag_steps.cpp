#include "wb/background.hpp"
#include "wb/detector.hpp"
#include "wb/features.hpp"
#include "wb/geometry.hpp"
#include "wb/grower.hpp"
#include "wb/image.hpp"
#include "wb/seeds.hpp"
#include "wb/similarity.hpp"

#define STB_IMAGE_IMPLEMENTATION
#include "stb_image.h"

#include <cstdio>
#include <string>
#include <vector>

static bool LoadPng(const char* path, std::vector<uint8_t>& bgra, int& w, int& h, int& stride) {
  int comp = 0;
  unsigned char* rgba = stbi_load(path, &w, &h, &comp, 4);
  if (!rgba) return false;
  stride = w * 4;
  bgra.assign(rgba, rgba + static_cast<size_t>(stride) * h);
  stbi_image_free(rgba);
  return true;
}

int main(int argc, char** argv) {
  if (argc < 2) {
    std::printf("usage: diag_steps <png> [l t r b]\n");
    return 1;
  }
  std::vector<uint8_t> bgra;
  int w = 0, h = 0, stride = 0;
  if (!LoadPng(argv[1], bgra, w, h, stride)) {
    std::printf("load fail\n");
    return 1;
  }
  wb::IntRect user_roi{8, 8, w - 8, h - 8};
  if (argc >= 6) {
    user_roi = {std::atoi(argv[2]), std::atoi(argv[3]), std::atoi(argv[4]), std::atoi(argv[5])};
  }

  wb::DetectorConfig cfg;
  const float dpi = 1.f;
  wb::IntRect grow{0, 0, w, h};
  std::printf("size=%dx%d roi=[%d,%d,%d,%d)\n", w, h, user_roi.left, user_roi.top, user_roi.right,
              user_roi.bottom);

  auto bgr = wb::BgraToBgr(wb::CopyBgraBuffer(bgra.data(), w, h, stride));
  auto feat = wb::ExtractFeatures(bgr, cfg, dpi, &grow);
  auto seeds = wb::SampleBackgroundSeeds(feat, user_roi, cfg, dpi);
  int acc = 0;
  for (const auto& s : seeds)
    if (s.accepted) ++acc;
  std::printf("[1] seeds=%zu accepted=%d\n", seeds.size(), acc);

  auto models = wb::EstimateBackgroundModels(seeds, cfg, &user_roi);
  std::printf("[2] models=%zu\n", models.size());
  for (size_t i = 0; i < models.size(); ++i) {
    const auto& m = models[i];
    std::printf("  model[%zu] conf=%.2f seeds=%zu Lab=(%.1f,%.1f,%.1f) rect_sup=%.2f de=%.1f/%.1f\n",
                i, m.confidence, m.seed_ids.size(), m.center_lab.L, m.center_lab.a, m.center_lab.b,
                m.rectangular_support_score, m.strong_delta_e, m.weak_delta_e);
  }
  if (models.empty()) return 0;

  for (size_t mi = 0; mi < models.size(); ++mi) {
    const auto& model = models[mi];
    auto sim = wb::BuildSimilarity(feat, model, grow, cfg);
    wb::GrownBackground grown;
    if (!wb::GrowBackground(seeds, model.seed_ids, sim, grow, cfg, grown)) {
      std::printf("[3] model[%zu] grow FAIL\n", mi);
      continue;
    }
    const bool ok_model = wb::IsWorkspaceBackgroundModel(grown, model, cfg);
    std::printf(
        "[3] model[%zu] px=%d bbox=[%d,%d,%d,%d) hole=%.2f fill=%.2f border=%.2f ok=%d\n", mi,
        grown.pixel_count, grown.bbox.left, grown.bbox.top, grown.bbox.right, grown.bbox.bottom,
        grown.hole_score, grown.bbox_fill_ratio, grown.touches_capture_border, (int)ok_model);
    if (!ok_model) continue;

    auto geo = wb::ExtractGeometry(grown, grow, cfg);
    std::printf("[4] model[%zu] outer_sides=%zu l_shapes=%zu bands=%zu\n", mi, geo.outer_sides.size(),
                geo.l_shapes.size(), geo.bands.size());
    for (const auto& s : geo.outer_sides) {
      std::printf("    side=%d coord=%d run=[%d,%d) cov=%.2f trunc=%d outer=%d\n", (int)s.side,
                  s.coord, s.run_start, s.run_end, s.coverage, (int)s.truncated,
                  (int)s.is_workspace_outer);
    }
    auto hyps = wb::BuildHypotheses(geo, model, 0, grown, grow, cfg);
    std::printf("[5] model[%zu] hyps=%zu\n", mi, hyps.size());
  }
  return 0;
}
