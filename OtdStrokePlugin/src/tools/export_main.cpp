#include "otd_stroke/c_api.h"

#include <cctype>
#include <chrono>
#include <filesystem>
#include <iostream>
#include <string>
#include <thread>
#include <unordered_set>

#ifdef _WIN32
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <shellapi.h>
#endif

namespace fs = std::filesystem;

namespace {

void pauseEnter(const char* msg = "Press Enter to exit...") {
    std::cerr << '\n' << msg << '\n';
    std::string line;
    std::getline(std::cin, line);
}

bool endsWithIgnoreCase(const std::string& value, const std::string& suffix) {
    if (value.size() < suffix.size()) {
        return false;
    }
    for (std::size_t i = 0; i < suffix.size(); ++i) {
        const auto a = static_cast<unsigned char>(value[value.size() - suffix.size() + i]);
        const auto b = static_cast<unsigned char>(suffix[i]);
        if (std::tolower(a) != std::tolower(b)) {
            return false;
        }
    }
    return true;
}

bool isStrokeBin(const fs::path& path) {
    return endsWithIgnoreCase(path.filename().string(), ".strokebin");
}

fs::path jsonBeside(const fs::path& strokeBin) {
    return strokeBin.parent_path() / (strokeBin.stem().string() + ".json");
}

bool exportOne(const fs::path& input) {
    if (!isStrokeBin(input)) {
        std::cerr << "skip (not .strokebin): " << input.string() << '\n';
        return false;
    }
    if (!fs::exists(input)) {
        std::cerr << "missing: " << input.string() << '\n';
        return false;
    }

    const auto output = jsonBeside(input);
    std::cout << "converting:\n  " << input.string() << "\n-> " << output.string() << '\n';

    const int ok = otd_stroke_export_json(input.string().c_str(), output.string().c_str());
    if (!ok) {
        std::cerr << "FAILED: " << input.string() << '\n';
        return false;
    }
    std::cout << "OK wrote " << output.string() << "\n\n";
    return true;
}

fs::path exeDirectory() {
#ifdef _WIN32
    wchar_t buf[MAX_PATH]{};
    const DWORD n = GetModuleFileNameW(nullptr, buf, MAX_PATH);
    if (n > 0 && n < MAX_PATH) {
        return fs::path(buf).parent_path();
    }
#endif
    return fs::current_path();
}

fs::path defaultDropFolder() {
    return exeDirectory() / "drop_strokebin_here";
}

void printBanner() {
    std::cout << "============================================\n";
    std::cout << "  ART Stroke Bin -> JSON Converter\n";
    std::cout << "============================================\n\n";
    std::cout << "How to use (pick one):\n";
    std::cout << "  1) Drag-drop one or more .strokebin onto this exe\n";
    std::cout << "     -> writes .json next to each original file\n";
    std::cout << "  2) Copy .strokebin into the watch folder below\n";
    std::cout << "     -> auto-converts to .json in that same folder\n";
    std::cout << "  3) CLI: otd_stroke_export file.strokebin\n";
    std::cout << "     or:  otd_stroke_export in.strokebin out.json\n\n";
}

int convertArgs(int argc, char** argv) {
    int okCount = 0;
    int failCount = 0;

    // Classic two-arg form: explicit output path.
    if (argc == 3 && !isStrokeBin(argv[2])) {
        const fs::path in(argv[1]);
        const fs::path out(argv[2]);
        std::cout << "converting:\n  " << in.string() << "\n-> " << out.string() << '\n';
        if (otd_stroke_export_json(in.string().c_str(), out.string().c_str())) {
            std::cout << "OK wrote " << out.string() << '\n';
            return 0;
        }
        std::cerr << "FAILED\n";
        return 2;
    }

    for (int i = 1; i < argc; ++i) {
        if (exportOne(argv[i])) {
            ++okCount;
        } else {
            ++failCount;
        }
    }

    std::cout << "done. ok=" << okCount << " failed=" << failCount << '\n';
    return failCount == 0 ? 0 : 2;
}

int watchFolder(const fs::path& folder) {
    std::error_code ec;
    fs::create_directories(folder, ec);

    printBanner();
    std::cout << "Watching folder:\n  " << folder.string() << "\n\n";
    std::cout << "Copy .strokebin files into this folder.\n";
    std::cout << "JSON will be written beside them automatically.\n";
    std::cout << "Press Ctrl+C to stop.\n\n";

#ifdef _WIN32
    ShellExecuteW(nullptr, L"open", folder.wstring().c_str(), nullptr, nullptr, SW_SHOWNORMAL);
#endif

    std::unordered_set<std::string> converted;
    for (const auto& entry : fs::directory_iterator(folder, ec)) {
        if (!entry.is_regular_file()) {
            continue;
        }
        const auto path = entry.path();
        if (!isStrokeBin(path)) {
            continue;
        }
        const auto key = fs::weakly_canonical(path, ec).string();
        if (exportOne(path)) {
            converted.insert(key.empty() ? path.string() : key);
        }
    }

    while (true) {
        for (const auto& entry : fs::directory_iterator(folder, ec)) {
            if (!entry.is_regular_file()) {
                continue;
            }
            const auto path = entry.path();
            if (!isStrokeBin(path)) {
                continue;
            }

            std::error_code ec1;
            const auto size1 = fs::file_size(path, ec1);
            std::this_thread::sleep_for(std::chrono::milliseconds(200));
            const auto size2 = fs::file_size(path, ec1);
            if (size1 != size2) {
                continue;
            }

            const auto key = fs::weakly_canonical(path, ec).string();
            const auto id = key.empty() ? path.string() : key;
            if (converted.count(id)) {
                continue;
            }

            const auto jsonPath = jsonBeside(path);
            if (fs::exists(jsonPath, ec)) {
                const auto binTime = fs::last_write_time(path, ec);
                const auto jsonTime = fs::last_write_time(jsonPath, ec);
                if (!ec && jsonTime >= binTime) {
                    converted.insert(id);
                    continue;
                }
            }

            if (exportOne(path)) {
                converted.insert(id);
            } else {
                converted.insert(id);
            }
        }
        std::this_thread::sleep_for(std::chrono::milliseconds(500));
    }
}

}  // namespace

int main(int argc, char** argv) {
    if (argc >= 2) {
        const int code = convertArgs(argc, argv);
        pauseEnter();
        return code;
    }

    return watchFolder(defaultDropFolder());
}
