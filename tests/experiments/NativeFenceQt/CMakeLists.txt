cmake_minimum_required(VERSION 3.24)
project(NativeFenceQtResearch LANGUAGES CXX)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
if(NOT DESKFLOW_SOURCE OR NOT DESKFLOW_BUILD)
  message(FATAL_ERROR "An isolated Deskflow source and build are required.")
endif()
find_package(Qt6 6.7 REQUIRED COMPONENTS Core Network)
add_executable(native-fence-qt harness.cpp
  "${DESKFLOW_SOURCE}/src/lib/deskflow/control/DeskToolboxControl.cpp"
  "${DESKFLOW_SOURCE}/src/lib/deskflow/control/UserLocalServer.cpp"
  "${DESKFLOW_SOURCE}/src/lib/deskflow/control/PreviewFenceState.cpp")
target_compile_definitions(native-fence-qt PRIVATE NIAN_NATIVE_FENCE_TEST)
target_include_directories(native-fence-qt PRIVATE "${DESKFLOW_SOURCE}/src/lib" "${DESKFLOW_BUILD}/src/lib")
foreach(lib IN ITEMS base mt arch common)
  target_link_libraries(native-fence-qt PRIVATE "${DESKFLOW_BUILD}/src/lib/${lib}/${lib}.lib")
endforeach()
target_link_libraries(native-fence-qt PRIVATE Qt6::Core Qt6::Network ws2_32 userenv Wtsapi32 wininet shlwapi version crypt32)

# Same patched source without the research macro: it must retain the v1 path.
add_executable(native-control-default "${CMAKE_CURRENT_LIST_DIR}/../../native-multipeer/harness.cpp"
  "${DESKFLOW_SOURCE}/src/lib/deskflow/control/DeskToolboxControl.cpp"
  "${DESKFLOW_SOURCE}/src/lib/deskflow/control/UserLocalServer.cpp")
target_include_directories(native-control-default PRIVATE "${DESKFLOW_SOURCE}/src/lib")
target_link_libraries(native-control-default PRIVATE Qt6::Core Qt6::Network)
