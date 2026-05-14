cmake_minimum_required(VERSION 3.17)

if(CMAKE_SOURCE_DIR STREQUAL CMAKE_BINARY_DIR)
    message(FATAL_ERROR "Do not build in-source. Please remove CMakeCache.txt and the CMakeFiles/ directory. Then build out-of-source.")
endif()

project(drone-algo
    VERSION 0.1.0
    LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
if(APPLE)
    add_compile_options(-mmacosx-version-min=10.15)
endif()

include(CTest)
include(GNUInstallDirs)
include(InstallRequiredSystemLibraries)
include(CMakePackageConfigHelpers)

list(APPEND CMAKE_MODULE_PATH "${CMAKE_CURRENT_LIST_DIR}/cmake")
include(BuildHelpers)

option(ENABLE_INSTALL
    "Should ${PROJECT_NAME} be added to the install list? Useful if included using add_subdirectory." ON)
option(ENABLE_TESTING "Should unit tests be compiled." OFF)

set(${PROJECT_NAME}_INSTALL_CMAKEDIR "${CMAKE_INSTALL_LIBDIR}/cmake/${PROJECT_NAME}"
    CACHE STRING "Path to install ${PROJECT_NAME} Config*.cmake files to.")

if(ENABLE_TESTING)
    include(FetchContent)

    FetchContent_Declare(
        googletest
        URL https://github.com/google/googletest/archive/refs/tags/v1.14.0.zip
        DOWNLOAD_EXTRACT_TIMESTAMP TRUE
    )

    FetchContent_MakeAvailable(googletest)

    include(GoogleTest)
    enable_testing()
endif()

add_subdirectory(src)

if(ENABLE_INSTALL)
    write_basic_package_version_file(
        ${PROJECT_NAME}ConfigVersion.cmake
        VERSION ${PROJECT_VERSION}
        COMPATIBILITY SameMajorVersion)

    configure_package_config_file(
        ${PROJECT_SOURCE_DIR}/cmake/Config.cmake.in
        ${PROJECT_BINARY_DIR}/${PROJECT_NAME}Config.cmake
        INSTALL_DESTINATION ${${PROJECT_NAME}_INSTALL_CMAKEDIR}
        NO_SET_AND_CHECK_MACRO
        NO_CHECK_REQUIRED_COMPONENTS_MACRO)

    install(
        TARGETS drone_algo
        EXPORT ${PROJECT_NAME}_Targets
        INCLUDES DESTINATION ${CMAKE_INSTALL_INCLUDEDIR})

    install(DIRECTORY src
        DESTINATION ${CMAKE_INSTALL_INCLUDEDIR}
        FILES_MATCHING PATTERN *.h)

    install(
        EXPORT ${PROJECT_NAME}_Targets
        NAMESPACE ${PROJECT_NAME}::
        FILE ${PROJECT_NAME}Targets.cmake
        DESTINATION ${${PROJECT_NAME}_INSTALL_CMAKEDIR})

    install(FILES
        ${CMAKE_CURRENT_BINARY_DIR}/${PROJECT_NAME}Config.cmake
        ${CMAKE_CURRENT_BINARY_DIR}/${PROJECT_NAME}ConfigVersion.cmake
        DESTINATION ${${PROJECT_NAME}_INSTALL_CMAKEDIR})
endif()
