#include "local_avoidance.h"

#include <algorithm>
#include <cmath>
#include <limits>

namespace drone::algorithms::avoidance {
namespace {

constexpr float kEpsilon = 1.0e-5F;
constexpr float kAvoidanceMargin = 0.15F;

bool IsFinite(float value) { return std::isfinite(value); }

bool IsFinite(Vec3f value) {
  return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
}

Vec3f Add(Vec3f a, Vec3f b) { return {a.x + b.x, a.y + b.y, a.z + b.z}; }

Vec3f Subtract(Vec3f a, Vec3f b) { return {a.x - b.x, a.y - b.y, a.z - b.z}; }

Vec3f Scale(Vec3f value, float scale) {
  return {value.x * scale, value.y * scale, value.z * scale};
}

float Dot(Vec3f a, Vec3f b) { return a.x * b.x + a.y * b.y + a.z * b.z; }

float LengthSquared(Vec3f value) { return Dot(value, value); }

float Length(Vec3f value) { return std::sqrt(LengthSquared(value)); }

Vec3f NormalizeOr(Vec3f value, Vec3f fallback) {
  const float length = Length(value);
  if (length <= kEpsilon)
    return fallback;
  return Scale(value, 1.0F / length);
}

Vec3f ClampLength(Vec3f value, float max_length) {
  const float length = Length(value);
  if (length <= max_length || length <= kEpsilon)
    return value;
  return Scale(value, max_length / length);
}

bool IsValidRequest(const LocalAvoidanceRequest &request) {
  if (!IsFinite(request.self_position) || !IsFinite(request.preferred_velocity))
    return false;
  if (!IsFinite(request.self_radius) || !IsFinite(request.max_speed) ||
      !IsFinite(request.time_horizon_seconds))
    return false;
  if (request.self_radius < 0.0F || request.max_speed < 0.0F ||
      request.time_horizon_seconds <= 0.0F)
    return false;
  for (const NeighborState &neighbor : request.neighbors) {
    if (!IsFinite(neighbor.position) || !IsFinite(neighbor.velocity) ||
        !IsFinite(neighbor.radius) || neighbor.radius < 0.0F) {
      return false;
    }
  }
  return true;
}

Vec3f AvoidanceCorrection(Vec3f self_position, Vec3f candidate_velocity,
                          float self_radius, float horizon,
                          const NeighborState &neighbor) {
  const Vec3f offset = Subtract(self_position, neighbor.position);
  const float distance = Length(offset);
  const float combined_radius = self_radius + neighbor.radius;
  const Vec3f away = NormalizeOr(
      offset, NormalizeOr(Subtract(candidate_velocity, neighbor.velocity),
                          {1.0F, 0.0F, 0.0F}));

  const float protected_radius = combined_radius + kAvoidanceMargin;
  if (distance < protected_radius) {
    const float overlap = protected_radius - distance;
    return Scale(away, overlap / std::max(horizon, kEpsilon));
  }

  const Vec3f relative_velocity =
      Subtract(candidate_velocity, neighbor.velocity);
  const float relative_speed_sq = LengthSquared(relative_velocity);
  if (relative_speed_sq <= kEpsilon)
    return {};

  const float closest_time = std::clamp(
      -Dot(Subtract(self_position, neighbor.position), relative_velocity) /
          relative_speed_sq,
      0.0F, horizon);
  const Vec3f closest_self =
      Add(self_position, Scale(candidate_velocity, closest_time));
  const Vec3f closest_neighbor =
      Add(neighbor.position, Scale(neighbor.velocity, closest_time));
  const Vec3f closest_offset = Subtract(closest_self, closest_neighbor);
  const float closest_distance = Length(closest_offset);

  if (closest_distance >= protected_radius)
    return {};

  const float urgency = 1.0F - (closest_time / horizon);
  const float needed_clearance = protected_radius - closest_distance;
  const Vec3f correction_dir = NormalizeOr(closest_offset, away);
  return Scale(correction_dir, needed_clearance * (0.5F + urgency));
}

} // namespace

LocalAvoidanceResult
ComputeLocalAvoidanceVelocity(const LocalAvoidanceRequest &request) {
  if (!IsValidRequest(request))
    return {};

  Vec3f velocity = ClampLength(request.preferred_velocity, request.max_speed);
  for (const NeighborState &neighbor : request.neighbors) {
    velocity = Add(velocity,
                   AvoidanceCorrection(request.self_position, velocity,
                                       request.self_radius,
                                       request.time_horizon_seconds, neighbor));
    velocity = ClampLength(velocity, request.max_speed);
  }

  return {true, velocity};
}

} // namespace drone::algorithms::avoidance

namespace {

drone::algorithms::avoidance::Vec3f ToVec3f(DroneVec3f value) {
  return {value.x, value.y, value.z};
}

DroneVec3f ToDroneVec3f(drone::algorithms::avoidance::Vec3f value) {
  return {value.x, value.y, value.z};
}

} // namespace

extern "C" std::int32_t DroneComputeLocalAvoidanceVelocity(
    DroneVec3f self_position, DroneVec3f preferred_velocity, float self_radius,
    float max_speed, float time_horizon_seconds,
    const DroneNeighborState *neighbors, std::int32_t neighbor_count,
    DroneVec3f *out_velocity) {
  if (neighbor_count < 0 || out_velocity == nullptr)
    return 0;
  if (neighbor_count > 0 && neighbors == nullptr)
    return 0;

  drone::algorithms::avoidance::LocalAvoidanceRequest request{
      ToVec3f(self_position), ToVec3f(preferred_velocity), self_radius,
      max_speed, time_horizon_seconds};
  request.neighbors.reserve(static_cast<std::size_t>(neighbor_count));
  for (std::int32_t i = 0; i < neighbor_count; ++i) {
    request.neighbors.push_back({ToVec3f(neighbors[i].position),
                                 ToVec3f(neighbors[i].velocity),
                                 neighbors[i].radius});
  }

  const auto result =
      drone::algorithms::avoidance::ComputeLocalAvoidanceVelocity(request);
  if (!result.success)
    return 0;

  *out_velocity = ToDroneVec3f(result.velocity);
  return 1;
}
