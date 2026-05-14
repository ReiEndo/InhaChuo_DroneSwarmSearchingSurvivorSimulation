#pragma once

#ifdef _WIN32
    #ifdef MOVEMENT_CORE_EXPORTS
        #define MOVEMENT_API __declspec(dllexport)
    #else
        #define MOVEMENT_API __declspec(dllimport)
    #endif
#elif defined(__EMSCRIPTEN__)
    #include <emscripten/emscripten.h>
    #define MOVEMENT_API EMSCRIPTEN_KEEPALIVE
#else
    #define MOVEMENT_API __attribute__((visibility("default")))
#endif

extern "C" {

struct MovementVec3
{
    float x;
    float y;
    float z;
};

struct MovementInput
{
    MovementVec3 currentPosition;
    MovementVec3 targetPosition;
    float maxDistanceDelta;
};

struct MovementResult
{
    MovementVec3 nextPosition;
    MovementVec3 direction;
    float remainingDistance;
    int reachedTarget;
};

MOVEMENT_API MovementResult CalculateNextMovement(MovementInput input);

}
