add_library(drone_algo SHARED)
target_sources(drone_algo
    PUBLIC
    $<INSTALL_INTERFACE:include/src/core/path.h>
    $<BUILD_INTERFACE:${CMAKE_CURRENT_LIST_DIR}/core/path.h>
    $<INSTALL_INTERFACE:include/src/algorithms/astar/planner.h>
    $<BUILD_INTERFACE:${CMAKE_CURRENT_LIST_DIR}/algorithms/astar/planner.h>
    $<INSTALL_INTERFACE:include/src/algorithms/frontier/frontier.h>
    $<BUILD_INTERFACE:${CMAKE_CURRENT_LIST_DIR}/algorithms/frontier/frontier.h>
    PRIVATE
    core/path.cc
    algorithms/astar/planner.cc
    algorithms/frontier/frontier.cc)
set_target_properties(drone_algo PROPERTIES OUTPUT_NAME drone_algo)
target_include_directories(drone_algo
    PUBLIC
    $<BUILD_INTERFACE:${PROJECT_SOURCE_DIR}>
    $<INSTALL_INTERFACE:include>)

if(ENABLE_TESTING)
    MAKE_CC_TEST(
        NAME astar_planner_test
        SRCS tests/astar_planner_test.cc
        DEPS drone_algo)
    MAKE_CC_TEST(
        NAME frontier_test
        SRCS tests/frontier_test.cc
        DEPS drone_algo)
endif()
