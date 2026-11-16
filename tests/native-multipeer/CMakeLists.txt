cmake_minimum_required(VERSION 3.24)
project(MultiPeerTransportHarness LANGUAGES CXX)
set(CMAKE_CXX_STANDARD 20)
find_package(Qt6 6.7 REQUIRED COMPONENTS Core Network)
add_executable(multi-peer-transport-harness
  "${CMAKE_CURRENT_LIST_DIR}/harness.cpp"
  "${DESKFLOW_SOURCE}/src/lib/deskflow/control/DeskToolboxControl.cpp"
  "${DESKFLOW_SOURCE}/src/lib/deskflow/control/UserLocalServer.cpp")
target_include_directories(multi-peer-transport-harness PRIVATE "${DESKFLOW_SOURCE}/src/lib")
target_link_libraries(multi-peer-transport-harness PRIVATE Qt6::Core Qt6::Network)
