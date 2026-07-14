cmake_minimum_required(VERSION 3.24)
project(WintabCollector VERSION 0.1.0 LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

option(WINTAB_COLLECTOR_BUILD_TESTS "Build unit tests" ON)

set(WINTAB_SDK_INCLUDE_DIR "${CMAKE_CURRENT_SOURCE_DIR}/third_party/wintab")

add_library(wintab_collector_lib STATIC
    src/types.cpp
    src/session_metadata.cpp
    src/packet_json.cpp
    src/wtlog_format.cpp
    src/wintab_api.cpp
    src/wintab_device.cpp
    src/wintab_context.cpp
    src/packet_decoder.cpp
    src/stroke_tracker.cpp
    src/async_recorder.cpp
    src/jsonl_writer.cpp
    src/binary_writer.cpp
    src/jsonl_exporter.cpp
    src/session_recovery.cpp
    src/session_io.cpp
    src/coordinate_mapper.cpp
    src/collector.cpp
)

target_include_directories(wintab_collector_lib
    PUBLIC
        "${CMAKE_CURRENT_SOURCE_DIR}/include"
    PRIVATE
        "${CMAKE_CURRENT_SOURCE_DIR}/src"
        "${WINTAB_SDK_INCLUDE_DIR}"
)

target_compile_definitions(wintab_collector_lib
    PRIVATE
        UNICODE
        _UNICODE
        WIN32_LEAN_AND_MEAN
        NOMINMAX
)

if(MSVC)
    target_compile_options(wintab_collector_lib PRIVATE /W4 /permissive-)
else()
    target_compile_options(wintab_collector_lib PRIVATE -Wall -Wextra -Wpedantic)
endif()

add_executable(wintab_collector src/main.cpp)
target_link_libraries(wintab_collector PRIVATE wintab_collector_lib)
target_compile_definitions(wintab_collector PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)

if(WINTAB_COLLECTOR_BUILD_TESTS)
    enable_testing()

    add_executable(coordinate_mapper_test tests/coordinate_mapper_test.cpp)
    target_link_libraries(coordinate_mapper_test PRIVATE wintab_collector_lib)
    target_include_directories(coordinate_mapper_test PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/src")
    target_compile_definitions(coordinate_mapper_test PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
    add_test(NAME coordinate_mapper_test COMMAND coordinate_mapper_test)

    add_executable(stroke_tracker_test tests/stroke_tracker_test.cpp)
    target_link_libraries(stroke_tracker_test PRIVATE wintab_collector_lib)
    target_include_directories(stroke_tracker_test PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/src")
    target_compile_definitions(stroke_tracker_test PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
    add_test(NAME stroke_tracker_test COMMAND stroke_tracker_test)

    add_executable(session_format_test tests/session_format_test.cpp)
    target_link_libraries(session_format_test PRIVATE wintab_collector_lib)
    target_include_directories(session_format_test PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/src")
    target_compile_definitions(session_format_test PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
    add_executable(wtlog_format_test tests/wtlog_format_test.cpp)
    target_link_libraries(wtlog_format_test PRIVATE wintab_collector_lib)
    target_include_directories(wtlog_format_test PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}/src")
    target_compile_definitions(wtlog_format_test PRIVATE UNICODE _UNICODE WIN32_LEAN_AND_MEAN NOMINMAX)
    add_test(NAME wtlog_format_test COMMAND wtlog_format_test)
endif()
