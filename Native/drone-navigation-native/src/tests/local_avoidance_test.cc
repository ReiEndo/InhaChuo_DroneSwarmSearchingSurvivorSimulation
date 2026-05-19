#include "../algorithms/avoidance/local_avoidance.h"

#include "gtest/gtest.h"

#include <cmath>

namespace avoidance = drone::algorithms::avoidance;

namespace {

float Length(avoidance::Vec3f value) {
  return std::sqrt(value.x * value.x + value.y * value.y + value.z * value.z);
}

} // namespace

TEST(LocalAvoidanceTest, KeepsPreferredVelocityWithoutNeighbors) {
  const auto result = avoidance::ComputeLocalAvoidanceVelocity(
      {{0.0F, 0.0F, 0.0F}, {1.0F, 0.0F, 0.0F}, 0.5F, 2.0F, 3.0F, {}});

  ASSERT_TRUE(result.success);
  EXPECT_FLOAT_EQ(result.velocity.x, 1.0F);
  EXPECT_FLOAT_EQ(result.velocity.y, 0.0F);
  EXPECT_FLOAT_EQ(result.velocity.z, 0.0F);
}

TEST(LocalAvoidanceTest, ClampsPreferredVelocityToMaxSpeed) {
  const auto result = avoidance::ComputeLocalAvoidanceVelocity(
      {{0.0F, 0.0F, 0.0F}, {3.0F, 4.0F, 0.0F}, 0.5F, 2.0F, 3.0F, {}});

  ASSERT_TRUE(result.success);
  EXPECT_NEAR(Length(result.velocity), 2.0F, 1.0e-5F);
}

TEST(LocalAvoidanceTest, SteersAwayFromCloseNeighbor) {
  const auto result = avoidance::ComputeLocalAvoidanceVelocity(
      {{0.0F, 0.0F, 0.0F},
       {1.0F, 0.0F, 0.0F},
       0.5F,
       2.0F,
       2.0F,
       {{{0.8F, 0.0F, 0.0F}, {0.0F, 0.0F, 0.0F}, 0.5F}}});

  ASSERT_TRUE(result.success);
  EXPECT_LT(result.velocity.x, 1.0F);
}

TEST(LocalAvoidanceTest, AvoidsPredictedCollision) {
  const auto result = avoidance::ComputeLocalAvoidanceVelocity(
      {{0.0F, 0.0F, 0.0F},
       {1.0F, 0.0F, 0.0F},
       0.4F,
       2.0F,
       4.0F,
       {{{2.0F, 0.2F, 0.0F}, {-1.0F, 0.0F, 0.0F}, 0.4F}}});

  ASSERT_TRUE(result.success);
  EXPECT_NE(result.velocity.y, 0.0F);
  EXPECT_LE(Length(result.velocity), 2.0F + 1.0e-5F);
}

TEST(LocalAvoidanceCTest, ComputesVelocityForUnityAbi) {
  const DroneNeighborState neighbors[] = {
      {{0.8F, 0.0F, 0.0F}, {0.0F, 0.0F, 0.0F}, 0.5F}};
  DroneVec3f out{};

  EXPECT_EQ(DroneComputeLocalAvoidanceVelocity({0.0F, 0.0F, 0.0F},
                                               {1.0F, 0.0F, 0.0F}, 0.5F, 2.0F,
                                               2.0F, neighbors, 1, &out),
            1);
  EXPECT_LT(out.x, 1.0F);
}

TEST(LocalAvoidanceCTest, RejectsInvalidInputs) {
  DroneVec3f out{};

  EXPECT_EQ(DroneComputeLocalAvoidanceVelocity({0.0F, 0.0F, 0.0F},
                                               {1.0F, 0.0F, 0.0F}, -1.0F, 2.0F,
                                               2.0F, nullptr, 0, &out),
            0);
  EXPECT_EQ(DroneComputeLocalAvoidanceVelocity({0.0F, 0.0F, 0.0F},
                                               {1.0F, 0.0F, 0.0F}, 0.5F, 2.0F,
                                               2.0F, nullptr, 1, &out),
            0);
  EXPECT_EQ(DroneComputeLocalAvoidanceVelocity({0.0F, 0.0F, 0.0F},
                                               {1.0F, 0.0F, 0.0F}, 0.5F, 2.0F,
                                               2.0F, nullptr, 0, nullptr),
            0);
}
