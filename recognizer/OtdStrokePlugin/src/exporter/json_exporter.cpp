#include "exporter/json_exporter.hpp"

#include "io/path_util.hpp"

#include <iomanip>
#include <sstream>

namespace otd_stroke {
namespace {

std::string escapeJson(const std::string& input) {
    std::ostringstream oss;
    for (const unsigned char ch : input) {
        switch (ch) {
        case '\"':
            oss << "\\\"";
            break;
        case '\\':
            oss << "\\\\";
            break;
        case '\b':
            oss << "\\b";
            break;
        case '\f':
            oss << "\\f";
            break;
        case '\n':
            oss << "\\n";
            break;
        case '\r':
            oss << "\\r";
            break;
        case '\t':
            oss << "\\t";
            break;
        default:
            if (ch < 0x20) {
                oss << "\\u" << std::hex << std::setw(4) << std::setfill('0')
                    << static_cast<int>(ch);
            } else {
                oss << ch;
            }
            break;
        }
    }
    return oss.str();
}

}  // namespace

std::string StrokeJsonExporter::exportSession(const RecordingSession& session) {
    std::ostringstream oss;
    oss << std::setprecision(17);
    oss << "{\n";
    oss << "  \"header\": {\n";
    oss << "    \"version\": " << session.header.version << ",\n";
    oss << "    \"createdAt\": \"" << detail::toIso8601(session.header.createdAtUnixMs) << "\",\n";
    oss << "    \"pluginVersion\": \"" << escapeJson(session.header.pluginVersion) << "\",\n";
    oss << "    \"sessionId\": \"" << escapeJson(session.sessionId) << "\",\n";
    oss << "    \"filePath\": \"" << escapeJson(session.filePath) << "\",\n";
    oss << "    \"device\": {\n";
    oss << "      \"name\": \"" << escapeJson(session.header.device.name) << "\",\n";
    oss << "      \"id\": \"" << escapeJson(session.header.device.id) << "\"\n";
    oss << "    }\n";
    oss << "  },\n";
    oss << "  \"segments\": [\n";

    for (std::size_t si = 0; si < session.segments.size(); ++si) {
        const auto& segment = session.segments[si];
        oss << "    {\n";
        oss << "      \"segmentId\": " << segment.segmentId << ",\n";
        oss << "      \"reason\": \"" << toString(segment.reason) << "\",\n";
        oss << "      \"startTimestamp\": \"" << detail::toIso8601(segment.startTimestampMs)
            << "\",\n";
        oss << "      \"endTimestamp\": \"" << detail::toIso8601(segment.endTimestampMs) << "\",\n";
        oss << "      \"pointCount\": " << segment.pointCount << ",\n";
        oss << "      \"writeStatus\": \"" << toString(segment.writeStatus) << "\",\n";
        oss << "      \"strokes\": [\n";

        for (std::size_t sti = 0; sti < segment.strokes.size(); ++sti) {
            const auto& stroke = segment.strokes[sti];
            oss << "        {\n";
            oss << "          \"strokeId\": " << stroke.strokeId << ",\n";
            oss << "          \"startTimestamp\": \"" << detail::toIso8601(stroke.startTimestampMs)
                << "\",\n";
            oss << "          \"endTimestamp\": \"" << detail::toIso8601(stroke.endTimestampMs)
                << "\",\n";
            oss << "          \"points\": [\n";
            for (std::size_t pi = 0; pi < stroke.points.size(); ++pi) {
                const auto& pt = stroke.points[pi];
                oss << "            {\n";
                oss << "              \"timestamp\": " << pt.timestampMs << ",\n";
                oss << "              \"timestampIso\": \"" << detail::toIso8601(pt.timestampMs)
                    << "\",\n";
                oss << "              \"deltaTime\": " << pt.deltaTimeMs << ",\n";
                oss << "              \"x\": " << pt.x << ",\n";
                oss << "              \"y\": " << pt.y << ",\n";
                oss << "              \"pressure\": " << pt.pressure << ",\n";
                oss << "              \"inContact\": " << (pt.inContact ? "true" : "false") << ",\n";
                oss << "              \"buttons\": " << pt.buttons << ",\n";
                oss << "              \"tiltX\": " << pt.tiltX << ",\n";
                oss << "              \"tiltY\": " << pt.tiltY << ",\n";
                oss << "              \"sequenceId\": " << pt.sequenceId << "\n";
                oss << "            }" << (pi + 1 < stroke.points.size() ? "," : "") << "\n";
            }
            oss << "          ]\n";
            oss << "        }" << (sti + 1 < segment.strokes.size() ? "," : "") << "\n";
        }

        oss << "      ]\n";
        oss << "    }" << (si + 1 < session.segments.size() ? "," : "") << "\n";
    }

    oss << "  ]\n";
    oss << "}\n";
    return oss.str();
}

}  // namespace otd_stroke
