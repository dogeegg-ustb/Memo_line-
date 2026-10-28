// Injected into CLIPStudioPaint.exe. Hooks the text and fill entry points of Direct2D, DirectWrite,
// GDI and GDI+ and writes what CSP draws (string, position, transform, brush colour, target window)
// as JSON lines next to this DLL. Phase 1: discovery of which entry points CSP actually uses.
#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <d2d1_1.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <dwrite_1.h>
#include <atomic>
#include <cstdio>
#include <mutex>
#include <string>
#include <unordered_map>
#include "MinHook.h"

#pragma comment(lib, "d2d1.lib")
#pragma comment(lib, "dwrite.lib")
#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")

namespace {

HMODULE g_self;
FILE* g_log;
std::mutex g_logLock;
thread_local int t_depth;

enum Api { aDrawText, aDrawTextLayout, aDrawGlyphRun, aDcDrawGlyphRun, aFillRect, aFillRounded, aClear, aBeginDraw,
    aEndDraw, aDrawBitmap, aDcDrawBitmap, aDcDrawImage, aCreateTextLayout, aCreateGdiLayout, aCreateTextFormat,
    aLayoutDraw, aBitmapRtGlyphRun, aExtTextOutW, aDrawTextW, aDrawTextExW, aGdipDrawString, aApiCount };
const char* kNames[aApiCount] = { "DrawText", "DrawTextLayout", "DrawGlyphRun", "DcDrawGlyphRun", "FillRectangle",
    "FillRoundedRectangle", "Clear", "BeginDraw", "EndDraw", "DrawBitmap", "DcDrawBitmap", "DcDrawImage",
    "CreateTextLayout", "CreateGdiCompatibleTextLayout", "CreateTextFormat", "TextLayoutDraw",
    "BitmapRenderTargetDrawGlyphRun", "ExtTextOutW", "DrawTextW", "DrawTextExW", "GdipDrawString" };
std::atomic<long long> g_count[aApiCount];
std::atomic<int> g_samples[aApiCount];
constexpr int kSampleLimit = 4000;

std::mutex g_layoutLock;
std::unordered_map<void*, std::wstring> g_layoutText;

std::string Utf8(const wchar_t* s, size_t n)
{
    if (!s || !n) return {};
    int len = WideCharToMultiByte(CP_UTF8, 0, s, (int)n, nullptr, 0, nullptr, nullptr);
    std::string out(len, '\0');
    WideCharToMultiByte(CP_UTF8, 0, s, (int)n, out.data(), len, nullptr, nullptr);
    return out;
}

std::string Json(const std::string& s)
{
    std::string o = "\"";
    for (unsigned char c : s)
    {
        if (c == '"' || c == '\\') { o += '\\'; o += (char)c; }
        else if (c < 0x20) { char b[8]; sprintf_s(b, "\\u%04x", c); o += b; }
        else o += (char)c;
    }
    return o + "\"";
}

bool Sample(Api api)
{
    g_count[api]++;
    return g_log && g_samples[api].fetch_add(1) < kSampleLimit;
}

void Line(const std::string& s)
{
    std::lock_guard<std::mutex> lock(g_logLock);
    if (!g_log) return;
    fputs(s.c_str(), g_log); fputc('\n', g_log);
}

std::string Num(double v) { char b[32]; sprintf_s(b, "%.2f", v); return b; }
std::string Ptr(const void* p) { char b[32]; sprintf_s(b, "\"%p\"", p); return b; }

// Window that a render target presents to, cached per target between BeginDraw calls.
std::mutex g_targetLock;
std::unordered_map<void*, HWND> g_targetHwnd;

HWND ResolveTarget(ID2D1RenderTarget* rt)
{
    HWND hwnd = nullptr;
    ID2D1HwndRenderTarget* h = nullptr;
    if (SUCCEEDED(rt->QueryInterface(__uuidof(ID2D1HwndRenderTarget), (void**)&h))) { hwnd = h->GetHwnd(); h->Release(); return hwnd; }
    ID2D1DeviceContext* dc = nullptr;
    if (SUCCEEDED(rt->QueryInterface(__uuidof(ID2D1DeviceContext), (void**)&dc)))
    {
        ID2D1Image* image = nullptr; dc->GetTarget(&image);
        ID2D1Bitmap1* bitmap = nullptr;
        if (image && SUCCEEDED(image->QueryInterface(__uuidof(ID2D1Bitmap1), (void**)&bitmap)))
        {
            IDXGISurface* surface = nullptr;
            if (SUCCEEDED(bitmap->GetSurface(&surface)) && surface)
            {
                IDXGISwapChain1* chain = nullptr;
                if (SUCCEEDED(surface->GetParent(__uuidof(IDXGISwapChain1), (void**)&chain)) && chain)
                { chain->GetHwnd(&hwnd); chain->Release(); }
                surface->Release();
            }
            bitmap->Release();
        }
        if (image) image->Release();
        dc->Release();
    }
    return hwnd;
}

HWND TargetOf(ID2D1RenderTarget* rt)
{
    std::lock_guard<std::mutex> lock(g_targetLock);
    auto it = g_targetHwnd.find(rt);
    return it == g_targetHwnd.end() ? nullptr : it->second;
}

std::string Context(ID2D1RenderTarget* rt)
{
    D2D1_MATRIX_3X2_F m; rt->GetTransform(&m);
    HWND hwnd = TargetOf(rt);
    char b[256];
    sprintf_s(b, "\"rt\":\"%p\",\"hwnd\":%llu,\"tx\":[%.3f,%.3f,%.3f,%.3f,%.2f,%.2f]", (void*)rt,
        (unsigned long long)(ULONG_PTR)hwnd, m._11, m._12, m._21, m._22, m._31, m._32);
    return b;
}

std::string Brush(ID2D1Brush* brush)
{
    ID2D1SolidColorBrush* solid = nullptr;
    if (brush && SUCCEEDED(brush->QueryInterface(__uuidof(ID2D1SolidColorBrush), (void**)&solid)))
    {
        auto c = solid->GetColor(); solid->Release();
        char b[96]; sprintf_s(b, ",\"color\":[%d,%d,%d,%d]", (int)(c.r * 255 + .5f), (int)(c.g * 255 + .5f),
            (int)(c.b * 255 + .5f), (int)(c.a * 255 + .5f));
        return b;
    }
    return brush ? ",\"color\":\"nonSolid\"" : "";
}

std::string Head(Api api) { return std::string("{\"api\":\"") + kNames[api] + "\",\"tid\":" + std::to_string(GetCurrentThreadId()); }

std::wstring LayoutText(void* layout)
{
    std::lock_guard<std::mutex> lock(g_layoutLock);
    auto it = g_layoutText.find(layout);
    return it == g_layoutText.end() ? L"" : it->second;
}

// ---- Direct2D ----
using DrawText_t = void(STDMETHODCALLTYPE*)(ID2D1RenderTarget*, const WCHAR*, UINT32, IDWriteTextFormat*, const D2D1_RECT_F*, ID2D1Brush*, D2D1_DRAW_TEXT_OPTIONS, DWRITE_MEASURING_MODE);
using DrawTextLayout_t = void(STDMETHODCALLTYPE*)(ID2D1RenderTarget*, D2D1_POINT_2F, IDWriteTextLayout*, ID2D1Brush*, D2D1_DRAW_TEXT_OPTIONS);
using DrawGlyphRun_t = void(STDMETHODCALLTYPE*)(ID2D1RenderTarget*, D2D1_POINT_2F, const DWRITE_GLYPH_RUN*, ID2D1Brush*, DWRITE_MEASURING_MODE);
using DcDrawGlyphRun_t = void(STDMETHODCALLTYPE*)(ID2D1DeviceContext*, D2D1_POINT_2F, const DWRITE_GLYPH_RUN*, const DWRITE_GLYPH_RUN_DESCRIPTION*, ID2D1Brush*, DWRITE_MEASURING_MODE);
using FillRect_t = void(STDMETHODCALLTYPE*)(ID2D1RenderTarget*, const D2D1_RECT_F*, ID2D1Brush*);
using FillRounded_t = void(STDMETHODCALLTYPE*)(ID2D1RenderTarget*, const D2D1_ROUNDED_RECT*, ID2D1Brush*);
using Clear_t = void(STDMETHODCALLTYPE*)(ID2D1RenderTarget*, const D2D1_COLOR_F*);
using BeginDraw_t = void(STDMETHODCALLTYPE*)(ID2D1RenderTarget*);
using EndDraw_t = HRESULT(STDMETHODCALLTYPE*)(ID2D1RenderTarget*, D2D1_TAG*, D2D1_TAG*);
using DrawBitmap_t = void(STDMETHODCALLTYPE*)(ID2D1RenderTarget*, ID2D1Bitmap*, const D2D1_RECT_F*, FLOAT, D2D1_BITMAP_INTERPOLATION_MODE, const D2D1_RECT_F*);
using DcDrawBitmap_t = void(STDMETHODCALLTYPE*)(ID2D1DeviceContext*, ID2D1Bitmap*, const D2D1_RECT_F*, FLOAT, D2D1_INTERPOLATION_MODE, const D2D1_RECT_F*, const D2D1_MATRIX_4X4_F*);
using DcDrawImage_t = void(STDMETHODCALLTYPE*)(ID2D1DeviceContext*, ID2D1Image*, const D2D1_POINT_2F*, const D2D1_RECT_F*, D2D1_INTERPOLATION_MODE, D2D1_COMPOSITE_MODE);

DrawText_t oDrawText; DrawTextLayout_t oDrawTextLayout; DrawGlyphRun_t oDrawGlyphRun; DcDrawGlyphRun_t oDcDrawGlyphRun;
FillRect_t oFillRect; FillRounded_t oFillRounded; Clear_t oClear; BeginDraw_t oBeginDraw; EndDraw_t oEndDraw;
DrawBitmap_t oDrawBitmap; DcDrawBitmap_t oDcDrawBitmap; DcDrawImage_t oDcDrawImage;

struct Guard { bool outer; Guard() : outer(t_depth++ == 0) {} ~Guard() { t_depth--; } };

void STDMETHODCALLTYPE hDrawText(ID2D1RenderTarget* rt, const WCHAR* s, UINT32 n, IDWriteTextFormat* f, const D2D1_RECT_F* r, ID2D1Brush* b, D2D1_DRAW_TEXT_OPTIONS o, DWRITE_MEASURING_MODE m)
{
    Guard g;
    if (g.outer && Sample(aDrawText))
        Line(Head(aDrawText) + "," + Context(rt) + ",\"rect\":[" + Num(r->left) + "," + Num(r->top) + "," + Num(r->right) + "," + Num(r->bottom) +
            "],\"text\":" + Json(Utf8(s, n)) + ",\"size\":" + Num(f ? f->GetFontSize() : 0) + Brush(b) + "}");
    oDrawText(rt, s, n, f, r, b, o, m);
}

void STDMETHODCALLTYPE hDrawTextLayout(ID2D1RenderTarget* rt, D2D1_POINT_2F p, IDWriteTextLayout* layout, ID2D1Brush* b, D2D1_DRAW_TEXT_OPTIONS o)
{
    Guard g;
    if (g.outer && Sample(aDrawTextLayout))
    {
        DWRITE_TEXT_METRICS tm{}; if (layout) layout->GetMetrics(&tm);
        Line(Head(aDrawTextLayout) + "," + Context(rt) + ",\"origin\":[" + Num(p.x) + "," + Num(p.y) + "],\"box\":[" + Num(tm.left) + "," +
            Num(tm.top) + "," + Num(tm.width) + "," + Num(tm.height) + "],\"text\":" + Json(Utf8(LayoutText(layout).c_str(), LayoutText(layout).size())) + Brush(b) + "}");
    }
    oDrawTextLayout(rt, p, layout, b, o);
}

std::string Glyphs(const DWRITE_GLYPH_RUN* run)
{
    std::string s = "[";
    for (UINT32 i = 0; run && i < run->glyphCount && i < 64; i++) { if (i) s += ","; s += std::to_string(run->glyphIndices[i]); }
    return s + "]";
}

void STDMETHODCALLTYPE hDrawGlyphRun(ID2D1RenderTarget* rt, D2D1_POINT_2F p, const DWRITE_GLYPH_RUN* run, ID2D1Brush* b, DWRITE_MEASURING_MODE m)
{
    Guard g;
    if (g.outer && Sample(aDrawGlyphRun))
        Line(Head(aDrawGlyphRun) + "," + Context(rt) + ",\"origin\":[" + Num(p.x) + "," + Num(p.y) + "],\"em\":" + Num(run ? run->fontEmSize : 0) +
            ",\"face\":" + Ptr(run ? run->fontFace : nullptr) + ",\"glyphs\":" + Glyphs(run) + Brush(b) + "}");
    oDrawGlyphRun(rt, p, run, b, m);
}

void STDMETHODCALLTYPE hDcDrawGlyphRun(ID2D1DeviceContext* dc, D2D1_POINT_2F p, const DWRITE_GLYPH_RUN* run, const DWRITE_GLYPH_RUN_DESCRIPTION* d, ID2D1Brush* b, DWRITE_MEASURING_MODE m)
{
    Guard g;
    if (g.outer && Sample(aDcDrawGlyphRun))
        Line(Head(aDcDrawGlyphRun) + "," + Context(dc) + ",\"origin\":[" + Num(p.x) + "," + Num(p.y) + "],\"em\":" + Num(run ? run->fontEmSize : 0) +
            ",\"face\":" + Ptr(run ? run->fontFace : nullptr) + ",\"glyphs\":" + Glyphs(run) +
            ",\"text\":" + Json(d && d->string ? Utf8(d->string, d->stringLength) : "") + Brush(b) + "}");
    oDcDrawGlyphRun(dc, p, run, d, b, m);
}

void STDMETHODCALLTYPE hFillRect(ID2D1RenderTarget* rt, const D2D1_RECT_F* r, ID2D1Brush* b)
{
    Guard g;
    if (g.outer && Sample(aFillRect))
        Line(Head(aFillRect) + "," + Context(rt) + ",\"rect\":[" + Num(r->left) + "," + Num(r->top) + "," + Num(r->right) + "," + Num(r->bottom) + "]" + Brush(b) + "}");
    oFillRect(rt, r, b);
}

void STDMETHODCALLTYPE hFillRounded(ID2D1RenderTarget* rt, const D2D1_ROUNDED_RECT* r, ID2D1Brush* b)
{
    Guard g;
    if (g.outer && Sample(aFillRounded))
        Line(Head(aFillRounded) + "," + Context(rt) + ",\"rect\":[" + Num(r->rect.left) + "," + Num(r->rect.top) + "," + Num(r->rect.right) + "," + Num(r->rect.bottom) + "]" + Brush(b) + "}");
    oFillRounded(rt, r, b);
}

void STDMETHODCALLTYPE hClear(ID2D1RenderTarget* rt, const D2D1_COLOR_F* c)
{
    Guard g;
    if (g.outer && Sample(aClear))
        Line(Head(aClear) + "," + Context(rt) + (c ? ",\"color\":[" + std::to_string((int)(c->r * 255)) + "," + std::to_string((int)(c->g * 255)) + "," +
            std::to_string((int)(c->b * 255)) + "," + std::to_string((int)(c->a * 255)) + "]" : "") + "}");
    oClear(rt, c);
}

void STDMETHODCALLTYPE hBeginDraw(ID2D1RenderTarget* rt)
{
    Guard g;
    oBeginDraw(rt);
    if (!g.outer) return;
    HWND hwnd = ResolveTarget(rt);
    { std::lock_guard<std::mutex> lock(g_targetLock); g_targetHwnd[rt] = hwnd; }
    if (Sample(aBeginDraw))
    {
        auto size = rt->GetSize(); auto px = rt->GetPixelSize(); FLOAT dx, dy; rt->GetDpi(&dx, &dy);
        Line(Head(aBeginDraw) + "," + Context(rt) + ",\"size\":[" + Num(size.width) + "," + Num(size.height) + "],\"pixels\":[" +
            std::to_string(px.width) + "," + std::to_string(px.height) + "],\"dpi\":" + Num(dx) + "}");
    }
}

HRESULT STDMETHODCALLTYPE hEndDraw(ID2D1RenderTarget* rt, D2D1_TAG* a, D2D1_TAG* b)
{
    Guard g;
    if (g.outer && Sample(aEndDraw)) Line(Head(aEndDraw) + "," + Context(rt) + "}");
    return oEndDraw(rt, a, b);
}

void STDMETHODCALLTYPE hDrawBitmap(ID2D1RenderTarget* rt, ID2D1Bitmap* bm, const D2D1_RECT_F* d, FLOAT op, D2D1_BITMAP_INTERPOLATION_MODE mode, const D2D1_RECT_F* s)
{
    Guard g;
    if (g.outer && Sample(aDrawBitmap))
    {
        auto px = bm ? bm->GetPixelSize() : D2D1_SIZE_U{};
        Line(Head(aDrawBitmap) + "," + Context(rt) + ",\"bitmap\":" + Ptr(bm) + ",\"pixels\":[" + std::to_string(px.width) + "," + std::to_string(px.height) + "]" +
            (d ? ",\"dest\":[" + Num(d->left) + "," + Num(d->top) + "," + Num(d->right) + "," + Num(d->bottom) + "]" : "") + "}");
    }
    oDrawBitmap(rt, bm, d, op, mode, s);
}

void STDMETHODCALLTYPE hDcDrawBitmap(ID2D1DeviceContext* dc, ID2D1Bitmap* bm, const D2D1_RECT_F* d, FLOAT op, D2D1_INTERPOLATION_MODE mode, const D2D1_RECT_F* s, const D2D1_MATRIX_4X4_F* p)
{
    Guard g;
    if (g.outer && Sample(aDcDrawBitmap))
    {
        auto px = bm ? bm->GetPixelSize() : D2D1_SIZE_U{};
        Line(Head(aDcDrawBitmap) + "," + Context(dc) + ",\"bitmap\":" + Ptr(bm) + ",\"pixels\":[" + std::to_string(px.width) + "," + std::to_string(px.height) + "]" +
            (d ? ",\"dest\":[" + Num(d->left) + "," + Num(d->top) + "," + Num(d->right) + "," + Num(d->bottom) + "]" : "") + "}");
    }
    oDcDrawBitmap(dc, bm, d, op, mode, s, p);
}

void STDMETHODCALLTYPE hDcDrawImage(ID2D1DeviceContext* dc, ID2D1Image* im, const D2D1_POINT_2F* o, const D2D1_RECT_F* r, D2D1_INTERPOLATION_MODE mode, D2D1_COMPOSITE_MODE c)
{
    Guard g;
    if (g.outer && Sample(aDcDrawImage))
        Line(Head(aDcDrawImage) + "," + Context(dc) + ",\"image\":" + Ptr(im) + (o ? ",\"origin\":[" + Num(o->x) + "," + Num(o->y) + "]" : "") + "}");
    oDcDrawImage(dc, im, o, r, mode, c);
}

// ---- DirectWrite ----
using CreateTextFormat_t = HRESULT(STDMETHODCALLTYPE*)(IDWriteFactory*, const WCHAR*, IDWriteFontCollection*, DWRITE_FONT_WEIGHT, DWRITE_FONT_STYLE, DWRITE_FONT_STRETCH, FLOAT, const WCHAR*, IDWriteTextFormat**);
using CreateTextLayout_t = HRESULT(STDMETHODCALLTYPE*)(IDWriteFactory*, const WCHAR*, UINT32, IDWriteTextFormat*, FLOAT, FLOAT, IDWriteTextLayout**);
using CreateGdiLayout_t = HRESULT(STDMETHODCALLTYPE*)(IDWriteFactory*, const WCHAR*, UINT32, IDWriteTextFormat*, FLOAT, FLOAT, FLOAT, const DWRITE_MATRIX*, BOOL, IDWriteTextLayout**);
using LayoutDraw_t = HRESULT(STDMETHODCALLTYPE*)(IDWriteTextLayout*, void*, IDWriteTextRenderer*, FLOAT, FLOAT);
using BitmapRtGlyphRun_t = HRESULT(STDMETHODCALLTYPE*)(IDWriteBitmapRenderTarget*, FLOAT, FLOAT, DWRITE_MEASURING_MODE, const DWRITE_GLYPH_RUN*, IDWriteRenderingParams*, COLORREF, RECT*);
CreateTextFormat_t oCreateTextFormat; CreateTextLayout_t oCreateTextLayout; CreateGdiLayout_t oCreateGdiLayout;
LayoutDraw_t oLayoutDraw; BitmapRtGlyphRun_t oBitmapRtGlyphRun;

HRESULT STDMETHODCALLTYPE hCreateTextFormat(IDWriteFactory* f, const WCHAR* family, IDWriteFontCollection* c, DWRITE_FONT_WEIGHT w, DWRITE_FONT_STYLE s, DWRITE_FONT_STRETCH st, FLOAT size, const WCHAR* locale, IDWriteTextFormat** out)
{
    HRESULT hr = oCreateTextFormat(f, family, c, w, s, st, size, locale, out);
    if (Sample(aCreateTextFormat))
        Line(Head(aCreateTextFormat) + ",\"family\":" + Json(Utf8(family, family ? wcslen(family) : 0)) + ",\"size\":" + Num(size) +
            ",\"weight\":" + std::to_string((int)w) + ",\"format\":" + Ptr(out ? *out : nullptr) + "}");
    return hr;
}

void RememberLayout(IDWriteTextLayout* layout, const WCHAR* s, UINT32 n)
{
    if (!layout) return;
    std::lock_guard<std::mutex> lock(g_layoutLock);
    if (g_layoutText.size() > 200000) g_layoutText.clear();
    g_layoutText[layout] = std::wstring(s ? s : L"", s ? n : 0);
}

HRESULT STDMETHODCALLTYPE hCreateTextLayout(IDWriteFactory* f, const WCHAR* s, UINT32 n, IDWriteTextFormat* fmt, FLOAT w, FLOAT h, IDWriteTextLayout** out)
{
    HRESULT hr = oCreateTextLayout(f, s, n, fmt, w, h, out);
    if (SUCCEEDED(hr) && out) RememberLayout(*out, s, n);
    if (Sample(aCreateTextLayout))
        Line(Head(aCreateTextLayout) + ",\"layout\":" + Ptr(out ? *out : nullptr) + ",\"text\":" + Json(Utf8(s, n)) + ",\"max\":[" + Num(w) + "," + Num(h) + "]}");
    return hr;
}

HRESULT STDMETHODCALLTYPE hCreateGdiLayout(IDWriteFactory* f, const WCHAR* s, UINT32 n, IDWriteTextFormat* fmt, FLOAT w, FLOAT h, FLOAT ppd, const DWRITE_MATRIX* m, BOOL natural, IDWriteTextLayout** out)
{
    HRESULT hr = oCreateGdiLayout(f, s, n, fmt, w, h, ppd, m, natural, out);
    if (SUCCEEDED(hr) && out) RememberLayout(*out, s, n);
    if (Sample(aCreateGdiLayout))
        Line(Head(aCreateGdiLayout) + ",\"layout\":" + Ptr(out ? *out : nullptr) + ",\"text\":" + Json(Utf8(s, n)) + ",\"max\":[" + Num(w) + "," + Num(h) + "]}");
    return hr;
}

HRESULT STDMETHODCALLTYPE hLayoutDraw(IDWriteTextLayout* layout, void* ctx, IDWriteTextRenderer* r, FLOAT x, FLOAT y)
{
    Guard g;
    if (g.outer && Sample(aLayoutDraw))
    {
        auto text = LayoutText(layout);
        Line(Head(aLayoutDraw) + ",\"layout\":" + Ptr(layout) + ",\"renderer\":" + Ptr(r) + ",\"origin\":[" + Num(x) + "," + Num(y) + "],\"text\":" + Json(Utf8(text.c_str(), text.size())) + "}");
    }
    return oLayoutDraw(layout, ctx, r, x, y);
}

HRESULT STDMETHODCALLTYPE hBitmapRtGlyphRun(IDWriteBitmapRenderTarget* t, FLOAT x, FLOAT y, DWRITE_MEASURING_MODE m, const DWRITE_GLYPH_RUN* run, IDWriteRenderingParams* p, COLORREF c, RECT* box)
{
    if (Sample(aBitmapRtGlyphRun))
        Line(Head(aBitmapRtGlyphRun) + ",\"origin\":[" + Num(x) + "," + Num(y) + "],\"em\":" + Num(run ? run->fontEmSize : 0) + ",\"glyphs\":" + Glyphs(run) + "}");
    return oBitmapRtGlyphRun(t, x, y, m, run, p, c, box);
}

// ---- GDI / GDI+ ----
using ExtTextOutW_t = BOOL(WINAPI*)(HDC, int, int, UINT, const RECT*, LPCWSTR, UINT, const INT*);
using DrawTextW_t = int(WINAPI*)(HDC, LPCWSTR, int, LPRECT, UINT);
using DrawTextExW_t = int(WINAPI*)(HDC, LPWSTR, int, LPRECT, UINT, LPDRAWTEXTPARAMS);
using GdipDrawString_t = int(WINAPI*)(void*, const WCHAR*, INT, void*, const void*, void*, void*);
ExtTextOutW_t oExtTextOutW; DrawTextW_t oDrawTextW; DrawTextExW_t oDrawTextExW; GdipDrawString_t oGdipDrawString;

std::string Hdc(HDC dc)
{
    HWND w = WindowFromDC(dc); POINT o{}; GetDCOrgEx(dc, &o);
    return ",\"hwnd\":" + std::to_string((unsigned long long)(ULONG_PTR)w) + ",\"dcOrg\":[" + std::to_string(o.x) + "," + std::to_string(o.y) + "]";
}

BOOL WINAPI hExtTextOutW(HDC dc, int x, int y, UINT o, const RECT* r, LPCWSTR s, UINT n, const INT* dx)
{
    Guard g;
    if (g.outer && !(o & ETO_GLYPH_INDEX) && n && Sample(aExtTextOutW))
        Line(Head(aExtTextOutW) + Hdc(dc) + ",\"origin\":[" + std::to_string(x) + "," + std::to_string(y) + "],\"text\":" + Json(Utf8(s, n)) + "}");
    else if (g.outer && n) g_count[aExtTextOutW]++;
    return oExtTextOutW(dc, x, y, o, r, s, n, dx);
}

int WINAPI hDrawTextW(HDC dc, LPCWSTR s, int n, LPRECT r, UINT f)
{
    Guard g;
    if (g.outer && Sample(aDrawTextW))
        Line(Head(aDrawTextW) + Hdc(dc) + ",\"text\":" + Json(Utf8(s, n < 0 ? (s ? wcslen(s) : 0) : n)) + "}");
    return oDrawTextW(dc, s, n, r, f);
}

int WINAPI hDrawTextExW(HDC dc, LPWSTR s, int n, LPRECT r, UINT f, LPDRAWTEXTPARAMS p)
{
    Guard g;
    if (g.outer && Sample(aDrawTextExW))
        Line(Head(aDrawTextExW) + Hdc(dc) + ",\"text\":" + Json(Utf8(s, n < 0 ? (s ? wcslen(s) : 0) : n)) + "}");
    return oDrawTextExW(dc, s, n, r, f, p);
}

int WINAPI hGdipDrawString(void* g0, const WCHAR* s, INT n, void* font, const void* rect, void* fmt, void* brush)
{
    Guard g;
    if (g.outer && Sample(aGdipDrawString))
        Line(Head(aGdipDrawString) + ",\"text\":" + Json(Utf8(s, n < 0 ? (s ? wcslen(s) : 0) : n)) + "}");
    return oGdipDrawString(g0, s, n, font, rect, fmt, brush);
}

// ---- installation ----
// MSVC compiles a pointer to a virtual member into a "vcall" thunk: mov rax,[rcx]; jmp [rax+disp].
template <class T> int VtIndex(T member)
{
    unsigned char* p = *reinterpret_cast<unsigned char**>(&member);
    for (int i = 0; i < 4 && p[0] == 0xE9; i++) p = p + 5 + *reinterpret_cast<int*>(p + 1);
    if (p[0] == 0x48 && p[1] == 0x8B && p[2] == 0x01 && p[3] == 0xFF)
    {
        if (p[4] == 0x20) return 0;
        if (p[4] == 0x60) return p[5] / sizeof(void*);
        if (p[4] == 0xA0) return *reinterpret_cast<int*>(p + 5) / (int)sizeof(void*);
    }
    return -1;
}

void* Slot(void* object, int index) { return index < 0 || !object ? nullptr : (*reinterpret_cast<void***>(object))[index]; }

int g_hooks;
template <class F> void Hook(const char* name, void* target, F detour, F* original)
{
    if (!target) { Line(std::string("{\"api\":\"hookError\",\"name\":\"") + name + "\",\"error\":\"noTarget\"}"); return; }
    MH_STATUS s = MH_CreateHook(target, reinterpret_cast<void*>(detour), reinterpret_cast<void**>(original));
    if (s == MH_ERROR_ALREADY_CREATED) return;
    if (s == MH_OK) s = MH_EnableHook(target);
    if (s == MH_OK) g_hooks++;
    Line(std::string("{\"api\":\"hook\",\"name\":\"") + name + "\",\"target\":" + Ptr(target) + ",\"status\":\"" + MH_StatusToString(s) + "\"}");
}

void HookRenderTarget(const char* kind, ID2D1RenderTarget* rt)
{
    if (!rt) { Line(std::string("{\"api\":\"hookError\",\"name\":\"") + kind + "\",\"error\":\"noRenderTarget\"}"); return; }
    std::string k = kind;
    Hook((k + ".DrawText").c_str(), Slot(rt, VtIndex(static_cast<void(STDMETHODCALLTYPE ID2D1RenderTarget::*)(const WCHAR*, UINT32, IDWriteTextFormat*, const D2D1_RECT_F*, ID2D1Brush*, D2D1_DRAW_TEXT_OPTIONS, DWRITE_MEASURING_MODE)>(&ID2D1RenderTarget::DrawText))), hDrawText, &oDrawText);
    Hook((k + ".DrawTextLayout").c_str(), Slot(rt, VtIndex(&ID2D1RenderTarget::DrawTextLayout)), hDrawTextLayout, &oDrawTextLayout);
    Hook((k + ".DrawGlyphRun").c_str(), Slot(rt, VtIndex(static_cast<void(STDMETHODCALLTYPE ID2D1RenderTarget::*)(D2D1_POINT_2F, const DWRITE_GLYPH_RUN*, ID2D1Brush*, DWRITE_MEASURING_MODE)>(&ID2D1RenderTarget::DrawGlyphRun))), hDrawGlyphRun, &oDrawGlyphRun);
    Hook((k + ".FillRectangle").c_str(), Slot(rt, VtIndex(static_cast<void(STDMETHODCALLTYPE ID2D1RenderTarget::*)(const D2D1_RECT_F*, ID2D1Brush*)>(&ID2D1RenderTarget::FillRectangle))), hFillRect, &oFillRect);
    Hook((k + ".FillRoundedRectangle").c_str(), Slot(rt, VtIndex(static_cast<void(STDMETHODCALLTYPE ID2D1RenderTarget::*)(const D2D1_ROUNDED_RECT*, ID2D1Brush*)>(&ID2D1RenderTarget::FillRoundedRectangle))), hFillRounded, &oFillRounded);
    Hook((k + ".Clear").c_str(), Slot(rt, VtIndex(static_cast<void(STDMETHODCALLTYPE ID2D1RenderTarget::*)(const D2D1_COLOR_F*)>(&ID2D1RenderTarget::Clear))), hClear, &oClear);
    Hook((k + ".BeginDraw").c_str(), Slot(rt, VtIndex(&ID2D1RenderTarget::BeginDraw)), hBeginDraw, &oBeginDraw);
    Hook((k + ".EndDraw").c_str(), Slot(rt, VtIndex(&ID2D1RenderTarget::EndDraw)), hEndDraw, &oEndDraw);
    Hook((k + ".DrawBitmap").c_str(), Slot(rt, VtIndex(static_cast<void(STDMETHODCALLTYPE ID2D1RenderTarget::*)(ID2D1Bitmap*, const D2D1_RECT_F*, FLOAT, D2D1_BITMAP_INTERPOLATION_MODE, const D2D1_RECT_F*)>(&ID2D1RenderTarget::DrawBitmap))), hDrawBitmap, &oDrawBitmap);
}

void InstallD2D()
{
    ID2D1Factory1* factory = nullptr;
    if (FAILED(D2D1CreateFactory(D2D1_FACTORY_TYPE_MULTI_THREADED, __uuidof(ID2D1Factory1), nullptr, (void**)&factory)))
    { Line("{\"api\":\"hookError\",\"name\":\"D2D1CreateFactory\"}"); return; }
    ID3D11Device* d3d = nullptr;
    for (auto type : { D3D_DRIVER_TYPE_HARDWARE, D3D_DRIVER_TYPE_WARP })
        if (SUCCEEDED(D3D11CreateDevice(nullptr, type, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION, &d3d, nullptr, nullptr))) break;
    IDXGIDevice* dxgi = nullptr; ID2D1Device* device = nullptr; ID2D1DeviceContext* dc = nullptr;
    if (d3d && SUCCEEDED(d3d->QueryInterface(__uuidof(IDXGIDevice), (void**)&dxgi)) && SUCCEEDED(factory->CreateDevice(dxgi, &device)) &&
        SUCCEEDED(device->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE, &dc)))
    {
        HookRenderTarget("DeviceContext", dc);
        Hook("DeviceContext.DcDrawGlyphRun", Slot(dc, VtIndex(static_cast<void(STDMETHODCALLTYPE ID2D1DeviceContext::*)(D2D1_POINT_2F, const DWRITE_GLYPH_RUN*, const DWRITE_GLYPH_RUN_DESCRIPTION*, ID2D1Brush*, DWRITE_MEASURING_MODE)>(&ID2D1DeviceContext::DrawGlyphRun))), hDcDrawGlyphRun, &oDcDrawGlyphRun);
        Hook("DeviceContext.DcDrawBitmap", Slot(dc, VtIndex(static_cast<void(STDMETHODCALLTYPE ID2D1DeviceContext::*)(ID2D1Bitmap*, const D2D1_RECT_F*, FLOAT, D2D1_INTERPOLATION_MODE, const D2D1_RECT_F*, const D2D1_MATRIX_4X4_F*)>(&ID2D1DeviceContext::DrawBitmap))), hDcDrawBitmap, &oDcDrawBitmap);
        Hook("DeviceContext.DcDrawImage", Slot(dc, VtIndex(static_cast<void(STDMETHODCALLTYPE ID2D1DeviceContext::*)(ID2D1Image*, const D2D1_POINT_2F*, const D2D1_RECT_F*, D2D1_INTERPOLATION_MODE, D2D1_COMPOSITE_MODE)>(&ID2D1DeviceContext::DrawImage))), hDcDrawImage, &oDcDrawImage);
    }
    else Line("{\"api\":\"hookError\",\"name\":\"DeviceContext\"}");
    // Other render-target kinds: MinHook reports ALREADY_CREATED where they share the implementation.
    auto props = D2D1::RenderTargetProperties(D2D1_RENDER_TARGET_TYPE_DEFAULT, D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED));
    ID2D1DCRenderTarget* dcrt = nullptr;
    if (SUCCEEDED(factory->CreateDCRenderTarget(&props, &dcrt))) HookRenderTarget("DCRenderTarget", dcrt);
    HWND window = CreateWindowExW(0, L"STATIC", L"", WS_POPUP, 0, 0, 8, 8, nullptr, nullptr, nullptr, nullptr);
    ID2D1HwndRenderTarget* hwndRt = nullptr;
    if (window && SUCCEEDED(factory->CreateHwndRenderTarget(props, D2D1::HwndRenderTargetProperties(window, D2D1::SizeU(8, 8)), &hwndRt)))
        HookRenderTarget("HwndRenderTarget", hwndRt);
    // The probe objects stay alive: releasing them is not needed and keeps their vtables mapped.
}

void InstallDWrite()
{
    IDWriteFactory* f = nullptr;
    if (FAILED(DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED, __uuidof(IDWriteFactory), (IUnknown**)&f))) { Line("{\"api\":\"hookError\",\"name\":\"DWriteCreateFactory\"}"); return; }
    Hook("DWrite.CreateTextFormat", Slot(f, VtIndex(&IDWriteFactory::CreateTextFormat)), hCreateTextFormat, &oCreateTextFormat);
    Hook("DWrite.CreateTextLayout", Slot(f, VtIndex(&IDWriteFactory::CreateTextLayout)), hCreateTextLayout, &oCreateTextLayout);
    Hook("DWrite.CreateGdiCompatibleTextLayout", Slot(f, VtIndex(&IDWriteFactory::CreateGdiCompatibleTextLayout)), hCreateGdiLayout, &oCreateGdiLayout);
    IDWriteTextFormat* format = nullptr; IDWriteTextLayout* layout = nullptr;
    if (SUCCEEDED(oCreateTextFormat ? oCreateTextFormat(f, L"Segoe UI", nullptr, DWRITE_FONT_WEIGHT_NORMAL, DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL, 12, L"", &format)
                                    : f->CreateTextFormat(L"Segoe UI", nullptr, DWRITE_FONT_WEIGHT_NORMAL, DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH_NORMAL, 12, L"", &format)) &&
        SUCCEEDED(oCreateTextLayout ? oCreateTextLayout(f, L"x", 1, format, 10, 10, &layout) : f->CreateTextLayout(L"x", 1, format, 10, 10, &layout)))
        Hook("DWrite.TextLayout.Draw", Slot(layout, VtIndex(&IDWriteTextLayout::Draw)), hLayoutDraw, &oLayoutDraw);
    IDWriteGdiInterop* interop = nullptr; IDWriteBitmapRenderTarget* brt = nullptr;
    HDC screen = GetDC(nullptr);
    if (SUCCEEDED(f->GetGdiInterop(&interop)) && SUCCEEDED(interop->CreateBitmapRenderTarget(screen, 8, 8, &brt)))
        Hook("DWrite.BitmapRenderTarget.DrawGlyphRun", Slot(brt, VtIndex(&IDWriteBitmapRenderTarget::DrawGlyphRun)), hBitmapRtGlyphRun, &oBitmapRtGlyphRun);
    ReleaseDC(nullptr, screen);
}

template <class F> void HookApi(const wchar_t* module, const char* name, F detour, F* original)
{
    HMODULE m = GetModuleHandleW(module);
    Hook(name, m ? reinterpret_cast<void*>(GetProcAddress(m, name)) : nullptr, detour, original);
}

void Counts()
{
    std::string s = "{\"api\":\"counts\",\"tick\":" + std::to_string(GetTickCount64());
    for (int i = 0; i < aApiCount; i++) s += ",\"" + std::string(kNames[i]) + "\":" + std::to_string(g_count[i].load());
    Line(s + "}");
    std::lock_guard<std::mutex> lock(g_logLock);
    if (g_log) fflush(g_log);
}

DWORD WINAPI Start(void*)
{
    wchar_t path[MAX_PATH]; GetModuleFileNameW(g_self, path, MAX_PATH);
    std::wstring log = path; log = log.substr(0, log.find_last_of(L'\\') + 1) + L"render_hook.jsonl";
    _wfopen_s(&g_log, log.c_str(), L"ab");
    Line("{\"api\":\"start\",\"pid\":" + std::to_string(GetCurrentProcessId()) + "}");
    if (MH_Initialize() != MH_OK) { Line("{\"api\":\"hookError\",\"name\":\"MH_Initialize\"}"); return 0; }
    InstallDWrite();
    InstallD2D();
    HookApi(L"gdi32.dll", "ExtTextOutW", hExtTextOutW, &oExtTextOutW);
    HookApi(L"user32.dll", "DrawTextW", hDrawTextW, &oDrawTextW);
    HookApi(L"user32.dll", "DrawTextExW", hDrawTextExW, &oDrawTextExW);
    HookApi(L"gdiplus.dll", "GdipDrawString", hGdipDrawString, &oGdipDrawString);
    Line("{\"api\":\"installed\",\"hooks\":" + std::to_string(g_hooks) + "}");
    Counts();
    for (;;) { Sleep(2000); Counts(); }
}

} // namespace

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_self = module;
        DisableThreadLibraryCalls(module);
        if (HANDLE t = CreateThread(nullptr, 0, Start, nullptr, 0, nullptr)) CloseHandle(t);
    }
    return TRUE;
}
