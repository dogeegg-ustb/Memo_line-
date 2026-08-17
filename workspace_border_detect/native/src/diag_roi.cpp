#include "wb/background.hpp"
#include "wb/detector.hpp"
#include "wb/features.hpp"
#include "wb/geometry.hpp"
#include "wb/grower.hpp"
#include "wb/image.hpp"
#include "wb/refine.hpp"
#include "wb/scoring.hpp"
#include "wb/seeds.hpp"
#include "wb/similarity.hpp"
#include "wb/validate.hpp"

#include <cstdio>
#include <fstream>
#include <string>
#include <vector>

namespace {

struct RawImage {
  int w = 0, h = 0, stride = 0;
  std::vector<uint8_t> bgra;
};

bool LoadRaw(const std::string& stem, RawImage& out) {
  std::ifstream meta(stem + ".meta.txt");
  if (!meta) {
    std::printf("FAIL open meta %s.meta.txt\n", stem.c_str());
    return false;
  }
  meta >> out.w >> out.h >> out.stride;
  if (out.w <= 0 || out.h <= 0) return false;
  if (out.stride <= 0) out.stride = out.w * 4;
  std::ifstream bin(stem + ".bgra", std::ios::binary);
  if (!bin) {
    std::printf("FAIL open %s.bgra\n", stem.c_str());
    return false;
  }
  out.bgra.resize(static_cast<size_t>(out.stride) * out.h);
  bin.read(reinterpret_cast<char*>(out.bgra.data()), static_cast<std::streamsize>(out.bgra.size()));
  if (!bin) {
    std::printf("FAIL read bgra bytes\n");
    return false;
  }
  return true;
}

const char* GradeName(wb::EvidenceGrade g) {
  switch (g) {
    case wb::EvidenceGrade::A:
      return "A";
    case wb::EvidenceGrade::B:
      return "B";
    case wb::EvidenceGrade::C_L:
      return "C_L";
    case wb::EvidenceGrade::C_II:
      return "C_II";
    default:
      return "None";
  }
}

void Diagnose(const std::string& stem) {
  std::printf("\n========== %s ==========\n", stem.c_str());
  RawImage raw;
  if (!LoadRaw(stem, raw)) return;

  // Images are already ROI crops → use nearly full frame as user ROI (inset 4px).
  wb::IntRect user_roi{4, 4, raw.w - 4, raw.h - 4};
  wb::DetectorConfig cfg;
  const float dpi = 1.f;
  wb::IntRect grow{0, 0, raw.w, raw.h};

  std::printf("[0] size=%dx%d user_roi=[%d,%d,%d,%d) grow=full capture\n", raw.w, raw.h,
              user_roi.left, user_roi.top, user_roi.right, user_roi.bottom);

  wb::DetectionInput in{};
  in.bgra = raw.bgra.data();
  in.width = raw.w;
  in.height = raw.h;
  in.stride = raw.stride;
  in.user_roi = user_roi;
  in.dpi_x = 96.f;
  in.dpi_y = 96.f;
  in.capture_id = "diag";

  // --- stage API path ---
  auto api = wb::DetectWorkspace(in, &cfg);
  std::printf("[API] status=%d (%s) msg=%s grade=%s conf=%.3f rect=[%d,%d,%d,%d)\n",
              static_cast<int>(api.status), wb::StatusName(api.status), api.message.c_str(),
              GradeName(api.grade), api.confidence, api.workspace_capture.left,
              api.workspace_capture.top, api.workspace_capture.right, api.workspace_capture.bottom);

  // --- detailed chain ---
  auto bgra = wb::CopyBgraBuffer(raw.bgra.data(), raw.w, raw.h, raw.stride);
  auto bgr = wb::BgraToBgr(bgra);
  auto feat = wb::ExtractFeatures(bgr, cfg, dpi, &grow);
  auto seeds = wb::SampleBackgroundSeeds(feat, user_roi, cfg, dpi);
  int acc = 0;
  for (const auto& s : seeds)
    if (s.accepted) ++acc;
  std::printf("[1] seeds=%zu accepted=%d\n", seeds.size(), acc);
  if (acc == 0) {
    std::printf("STOP: no accepted seeds\n");
    return;
  }

  auto models = wb::EstimateBackgroundModels(seeds, cfg, &user_roi);
  std::printf("[2] models=%zu\n", models.size());
  if (models.empty()) {
    std::printf("STOP: NoStableWorkspaceBackground\n");
    return;
  }
  for (size_t i = 0; i < models.size(); ++i) {
    const auto& m = models[i];
    std::printf("  model[%zu] conf=%.2f seeds=%zu de=%.1f/%.1f Lab=(%.1f,%.1f,%.1f) rect_sup=%.2f\n",
                i, m.confidence, m.seed_ids.size(), m.strong_delta_e, m.weak_delta_e, m.center_lab.L,
                m.center_lab.a, m.center_lab.b, m.rectangular_support_score);
  }

  bool any_grown = false;
  std::vector<wb::Hypothesis> all_hyps;
  std::vector<wb::SideSegment> all_sides;
  std::vector<wb::BackgroundModel> accepted_models;
  std::vector<wb::GrownBackground> accepted_grown;

  for (size_t mi = 0; mi < models.size(); ++mi) {
    const auto& model = models[mi];
    auto sim = wb::BuildSimilarity(feat, model, grow, cfg);
    wb::GrownBackground grown;
    if (!wb::GrowBackground(seeds, model.seed_ids, sim, grow, cfg, grown)) {
      std::printf("[3] model[%zu] grow FAIL\n", mi);
      continue;
    }
    any_grown = true;
    const bool ok_model = wb::IsWorkspaceBackgroundModel(grown, model, cfg);
    std::printf(
        "[3] model[%zu] grown px=%d bbox=[%d,%d,%d,%d) hole=%.2f fill=%.2f border=%.2f "
        "workspace_bg=%d\n",
        mi, grown.pixel_count, grown.bbox.left, grown.bbox.top, grown.bbox.right, grown.bbox.bottom,
        grown.hole_score, grown.bbox_fill_ratio, grown.touches_capture_border, (int)ok_model);
    if (!ok_model) continue;

    auto geo = wb::ExtractGeometry(grown, grow, cfg);
    std::printf("[4] model[%zu] sides=%zu bands=%zu L=%zu coverage=%.2f\n", mi, geo.outer_sides.size(),
                geo.bands.size(), geo.l_shapes.size(), geo.coverage);
    for (const auto& s : geo.outer_sides) {
      std::printf("    side=%d coord=%d run=[%d,%d) cov=%.2f trunc=%d mad=%.2f out=%.2f tr=%.2f\n",
                  (int)s.side, s.coord, s.run_start, s.run_end, s.coverage, (int)s.truncated,
                  s.coordinate_mad, s.outside_score, s.transition_score);
    }

    const int accepted_index = static_cast<int>(accepted_models.size());
    auto hyps = wb::BuildHypotheses(geo, model, accepted_index, grown, grow, cfg);
    std::printf("[5] model[%zu] raw_hyps=%zu\n", mi, hyps.size());
    for (auto& h : hyps) {
      const bool drop_c =
          h.endpoints_truncated &&
          (h.grade == wb::EvidenceGrade::C_L || h.grade == wb::EvidenceGrade::C_II);
      std::printf("    grade=%s rect=[%d,%d,%d,%d) trunc=%d %s\n", GradeName(h.grade), h.rect.left,
                  h.rect.top, h.rect.right, h.rect.bottom, (int)h.endpoints_truncated,
                  drop_c ? "DROP(C trunc)" : "keep");
      if (!drop_c) all_hyps.push_back(std::move(h));
    }
    for (const auto& s : geo.outer_sides) all_sides.push_back(s);
    accepted_models.push_back(model);
    accepted_grown.push_back(std::move(grown));
  }

  if (!any_grown) {
    std::printf("STOP: NoConnectedBackgroundEvidence\n");
    return;
  }
  if (accepted_models.empty() || all_hyps.empty()) {
    std::printf("STOP: InsufficientGeometry / no workspace bg model or all C truncated\n");
    return;
  }

  auto sel = wb::SelectBestHypothesis(all_hyps, all_sides, cfg);
  if (!sel.best) {
    std::printf("STOP: select fail reason=%s ranked=%zu\n", sel.reason.c_str(), sel.ranked.size());
    for (size_t i = 0; i < std::min<size_t>(sel.ranked.size(), 5); ++i) {
      const auto& h = sel.ranked[i];
      std::printf("  ranked[%zu] grade=%s conf=%.3f rect=[%d,%d,%d,%d)\n", i, GradeName(h.grade),
                  h.confidence, h.rect.left, h.rect.top, h.rect.right, h.rect.bottom);
    }
    return;
  }
  std::printf("[6] selected grade=%s conf=%.3f iou_margin=%.3f rect=[%d,%d,%d,%d)\n",
              GradeName(sel.best->grade), sel.best->confidence, sel.margin,
              sel.best->rect.left, sel.best->rect.top, sel.best->rect.right, sel.best->rect.bottom);
  for (size_t i = 0; i < std::min<size_t>(sel.ranked.size(), 5); ++i) {
    const auto& h = sel.ranked[i];
    std::printf("  ranked[%zu] grade=%s conf=%.3f rect=[%d,%d,%d,%d)\n", i, GradeName(h.grade),
                h.confidence, h.rect.left, h.rect.top, h.rect.right, h.rect.bottom);
  }

  wb::Hypothesis best = *sel.best;
  wb::BackgroundModel refine_model = accepted_models[0];
  wb::GrownBackground* best_grown_ptr = &accepted_grown[0];
  if (best.model_index >= 0 && best.model_index < static_cast<int>(accepted_models.size())) {
    refine_model = accepted_models[best.model_index];
    best_grown_ptr = &accepted_grown[best.model_index];
  }

  wb::IntRect refined;
  if (!wb::RefineRectangle(best.rect, feat, refine_model, cfg, dpi, refined)) {
    std::printf("STOP: RefinementFailed coarse=[%d,%d,%d,%d)\n", best.rect.left, best.rect.top,
                best.rect.right, best.rect.bottom);
    return;
  }
  std::printf("[7] refined=[%d,%d,%d,%d)\n", refined.left, refined.top, refined.right,
              refined.bottom);

  auto val = wb::ValidateRectangle(refined, best, feat, refine_model, &best_grown_ptr->mask, cfg);
  std::printf("[8] validate ok=%d conf=%.3f\n", (int)val.ok, val.confidence);
  for (const auto& kv : val.metrics) {
    std::printf("    %s=%.4f\n", kv.first.c_str(), kv.second);
  }
  if (!val.ok) std::printf("STOP: IndependentValidationFailed\n");
  else std::printf("PASS chain\n");
}

}  // namespace

int main(int argc, char** argv) {
  const char* base = R"(d:\ART_line A\ART_line\workspace_border_detect\testdata\roi)";
  if (argc >= 2) {
    Diagnose(argv[1]);
    return 0;
  }
  for (int i = 1; i <= 3; ++i) {
    Diagnose(std::string(base) + std::to_string(i));
  }
  return 0;
}
