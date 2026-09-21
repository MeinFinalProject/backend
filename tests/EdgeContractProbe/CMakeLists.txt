cmake_minimum_required(VERSION 3.24)
project(EdgeContractProbe LANGUAGES CXX)
if(NOT WIN32 OR NOT DEFINED EDGE_SOURCE_DIR)
  message(FATAL_ERROR "Requires Windows and -DEDGE_SOURCE_DIR=<edge-client checkout>")
endif()
add_executable(edge_contract_probe
  main.cpp
  "${EDGE_SOURCE_DIR}/edge_app/domain/gallery_snapshot.cpp"
  "${EDGE_SOURCE_DIR}/edge_app/domain/uuid_v7.cpp"
  "${EDGE_SOURCE_DIR}/edge_app/security/device_credential.cpp"
  "${EDGE_SOURCE_DIR}/edge_app/security/local_data.cpp"
  "${EDGE_SOURCE_DIR}/edge_app/storage/database.cpp"
  "${EDGE_SOURCE_DIR}/edge_app/storage/outbox_repository.cpp"
  "${EDGE_SOURCE_DIR}/edge_app/storage/gallery_repository.cpp"
  "${EDGE_SOURCE_DIR}/edge_app/sync/attendance_sync.cpp"
  "${EDGE_SOURCE_DIR}/edge_app/sync/gallery_sync.cpp"
  "${EDGE_SOURCE_DIR}/edge_app/sync/retry_policy.cpp"
  "${EDGE_SOURCE_DIR}/edge_app/sync/http_client.cpp"
  "${EDGE_SOURCE_DIR}/edge_app/sync/wire_format.cpp")
target_include_directories(edge_contract_probe PRIVATE "${EDGE_SOURCE_DIR}/edge_app")
target_compile_features(edge_contract_probe PRIVATE cxx_std_20)
target_compile_definitions(edge_contract_probe PRIVATE NOMINMAX WIN32_LEAN_AND_MEAN)
target_compile_options(edge_contract_probe PRIVATE /EHsc /W4)
target_link_libraries(edge_contract_probe PRIVATE bcrypt crypt32 windowsapp advapi32 winsqlite3)
