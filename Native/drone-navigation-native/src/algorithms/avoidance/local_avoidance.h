#pragma once

#include <cstdint>
#include <vector>

namespace drone::algorithms::avoidance {

struct Vec3f {
  float x;
  float y;
  float z;

  bool operator==(const Vec3f &other) const {
    return x == other.x && y == other.y && z == other.z;
  }
};

struct NeighborState {
  Vec3f position;
  Vec3f velocity;
  float radius = 0.0F;
};

struct LocalAvoidanceRequest {
  Vec3f self_position;
  Vec3f preferred_velocity;
  float self_radius = 0.0F;
  float max_speed = 0.0F;
  float time_horizon_seconds = 1.0F;
  std::vector<NeighborState> neighbors;
};

struct LocalAvoidanceResult {
  bool success = false;
  Vec3f velocity{};
};

/**
 * Computes a local collision-avoidance velocity from nearby drone states.
 *
 * This is intentionally lighter than full ORCA linear programming, but follows
 * the same local-reciprocal idea: keep the preferred velocity unless a neighbor
 * is too close or predicted to violate the combined safety radius soon.
 */
[[nodiscard]] LocalAvoidanceResult
ComputeLocalAvoidanceVelocity(const LocalAvoidanceRequest &request);

} // namespace drone::algorithms::avoidance

extern "C" {

struct DroneVec3f {
  float x;
  float y;
  float z;
};

struct DroneNeighborState {
  DroneVec3f position;
  DroneVec3f velocity;
  float radius;
};

/**
 * Unity entry point for local drone-to-drone avoidance.
 *
 * Returns 1 and writes out_velocity on success. Returns 0 for invalid inputs.
 */
std::int32_t DroneComputeLocalAvoidanceVelocity(
    DroneVec3f self_position, DroneVec3f preferred_velocity, float self_radius,
    float max_speed, float time_horizon_seconds,
    const DroneNeighborState *neighbors, std::int32_t neighbor_count,
    DroneVec3f *out_velocity);

} // extern "C"
