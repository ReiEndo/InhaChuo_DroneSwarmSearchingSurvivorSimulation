include(CMakeParseArguments)

function(make_cc_test)
    if(NOT ENABLE_TESTING)
        return()
    endif()

    cmake_parse_arguments(MAKE_CC_TEST
        ""
        "NAME"
        "SRCS;DEPS;DATA"
        ${ARGN})

    add_executable(${MAKE_CC_TEST_NAME})
    target_sources(${MAKE_CC_TEST_NAME} PRIVATE ${MAKE_CC_TEST_SRCS})
    target_link_libraries(${MAKE_CC_TEST_NAME}
        PRIVATE ${MAKE_CC_TEST_DEPS} gtest gmock gtest_main)
    gtest_discover_tests(${MAKE_CC_TEST_NAME})

    file(RELATIVE_PATH dest_dir ${PROJECT_SOURCE_DIR} ${CMAKE_CURRENT_SOURCE_DIR})
    install(TARGETS ${MAKE_CC_TEST_NAME} DESTINATION tests/${dest_dir})
    foreach(data_file ${MAKE_CC_TEST_DATA})
        get_filename_component(subpath ${data_file} DIRECTORY)
        install(FILES ${data_file} DESTINATION tests/${dest_dir}/${subpath})
        configure_file(${data_file} ${data_file} COPYONLY)
    endforeach()
endfunction()
