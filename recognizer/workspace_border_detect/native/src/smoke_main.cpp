#include "wb/c_api.h"

#include <cstdio>
#include <cstring>
#include <vector>

// Minimal synthetic A-grade smoke: dark workspace bg with lighter canvas hole.
static void FillRect(std::vector<unsigned char>& buf, int w, int h, int stride, int l, int t,
                     int r, int b, unsigned char B, unsigned char G, unsigned char R) {
  for (int y = t; y < b && y < h; ++y) {
    for (int x = l; x < r && x < w; ++x) {
      unsigned char* p = buf.data() + y * stride + x * 4;
      p[0] = B;
      p[1] = G;
      p[2] = R;
      p[3] = 255;
    }
  }
}

int main() {
  const int w = 320;
  const int h = 240;
  const int stride = w * 4;
  std::vector<unsigned char> img(static_cast<size_t>(stride) * h, 0);

  // Outside UI (gray)
  FillRect(img, w, h, stride, 0, 0, w, h, 90, 90, 90);
  // Workspace background (dark)
  FillRect(img, w, h, stride, 40, 30, 280, 210, 32, 32, 36);
  // Canvas hole (light)
  FillRect(img, w, h, stride, 70, 55, 250, 185, 245, 245, 248);

  WbDetectRequest req{};
  req.bgra = img.data();
  req.width = w;
  req.height = h;
  req.stride = stride;
  req.user_roi = {50, 40, 270, 200};
  req.dpi_x = 96.f;
  req.dpi_y = 96.f;
  req.origin_x = 0;
  req.origin_y = 0;
  req.capture_id = "smoke_A_dark";

  WbDetectResult result{};
  const int st = wb_detect(&req, &result);
  std::printf("status=%d (%s)\n", result.status, wb_status_name(result.status));
  std::printf("capture=[%d,%d,%d,%d]\n", result.workspace_capture.left, result.workspace_capture.top,
              result.workspace_capture.right, result.workspace_capture.bottom);
  std::printf("screen=[%d,%d,%d,%d]\n", result.workspace_screen.left, result.workspace_screen.top,
              result.workspace_screen.right, result.workspace_screen.bottom);
  std::printf("grade=%d conf=%.3f msg=%s\n", result.evidence_grade, result.confidence,
              result.message);

  if (st == 0 && result.status == 0) {
    std::printf("SMOKE PASS\n");
    return 0;
  }
  std::printf("SMOKE FAIL\n");
  return 1;
}
