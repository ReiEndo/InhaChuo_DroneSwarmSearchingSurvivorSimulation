add_library(drone_algo SHARED)
target_sources(drone_algo
    PUBLIC
    $<INSTALL_INTERFACE:include/src/core/path.h>
    $<BUILD_INTERFACE:${CMAKE_CURRENT_LIST_DIR}/core/path.h>
    $<INSTALL_INTERFACE:include/src/core/local_map.h>
    $<BUILD_INTERFACE:${CMAKE_CURRENT_LIST_DIR}/core/local_map.h>
    $<INSTALL_INTERFACE:include/src/core/target_report.h>
    $<BUILD_INTERFACE:${CMAKE_CURRENT_LIST_DIR}/core/target_report.h>
    $<INSTALL_INTERFACE:include/src/algorithms/astar/planner.h>
    $<BUILD_INTERFACE:${CMAKE_CURRENT_LIST_DIR}/algorithms/astar/planner.h>
    $<INSTALL_INTERFACE:include/src/algorithms/theta_star/planner.h>
    $<BUILD_INTERFACE:${CMAKE_CURRENT_LIST_DIR}/algorithms/theta_star/planner.h>
    $<INSTALL_INTERFACE:include/src/algorithms/frontier/frontier.h>
    $<BUILD_INTERFACE:${CMAKE_CURRENT_LIST_DIR}/algorithms/frontier/frontier.h>
    $<INSTALL_INTERFACE:include/src/algorithms/avoidance/local_avoidance.h>
    $<BUILD_INTERFACE:${CMAKE_CURRENT_LIST_DIR}/algorithms/avoidance/local_avoidance.h>
    PRIVATE
    core/local_map.cc
    core/path.cc
    core/target_report.cc
    algorithms/astar/planner.cc
    algorithms/theta_star/planner.cc
    algorithms/frontier/frontier.cc
    algorithms/frontier/frontier_c_api.cc
    algorithms/avoidance/local_avoidance.cc)
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
        NAME theta_star_planner_test
        SRCS tests/theta_star_planner_test.cc
        DEPS drone_algo)
    MAKE_CC_TEST(
        NAME frontier_test
        SRCS tests/frontier_test.cc
        DEPS drone_algo)
    MAKE_CC_TEST(
        NAME local_map_test
        SRCS tests/local_map_test.cc
        DEPS drone_algo)
    MAKE_CC_TEST(
        NAME local_avoidance_test
        SRCS tests/local_avoidance_test.cc
        DEPS drone_algo)
    MAKE_CC_TEST(
        NAME target_report_test
        SRCS tests/target_report_test.cc
        DEPS drone_algo)
endif()
