﻿using System;
using UnityEngine;
using Random = UnityEngine.Random;
using UnityStandardAssets.Utility;
using UnityStandardAssets.Vehicles;
using System.Collections.Generic;

namespace VRAVE
{
	//	[RequireComponent(typeof (CarController))]
	//	[RequireComponent(typeof (Sensors))]
	//	[RequireComponent(typeof (ObstacleHandler))]
	public class CarAIControl : MonoBehaviour
	{
		public enum BrakeCondition
		{
			NeverBrake,
			// the car simply accelerates at full throttle all the time.
			TargetDirectionDifference,
			// the car will brake according to the upcoming change in direction of the target. Useful for route-based AI, slowing for corners.
			TargetDistance,
			// the car will brake as it approaches its target, regardless of the target's direction. Useful if you want the car to
			// head for a stationary target and come to rest when it arrives there.
			AvoidTarget
		}

		// This script provides input to the car controller in the same way that the user control script does.
		// As such, it is really 'driving' the car, with no special physics or animation tricks to make the car behave properly.

		// "wandering" is used to give the cars a more human, less robotic feel. They can waver slightly
		// in speed and direction while driving towards their target.
		[SerializeField] [Range (0, 1)] private float m_CautiousSpeedFactor = 0.05f;
		// percentage of max speed to use when being maximally cautious
		[SerializeField] [Range (0, 180)] private float m_CautiousMaxAngle = 50f;
		// angle of approaching corner to treat as warranting maximum caution
		[SerializeField] private float m_CautiousMaxDistance = 100f;
		// distance at which distance-based cautiousness begins
		[SerializeField] private float m_CautiousAngularVelocityFactor = 30f;
		// how cautious the AI should be when considering its own current angular velocity (i.e. easing off acceleration if spinning!)
		[SerializeField] private float m_SteerSensitivity = 0.05f;
		// how sensitively the AI uses steering input to turn to the desired direction
		[SerializeField] private float m_AccelSensitivity = 0.04f;
		// How sensitively the AI uses the accelerator to reach the current desired speed
		[SerializeField] private float m_BrakeSensitivity = 1f;
		// How sensitively the AI uses the brake to reach the current desired speed
		[SerializeField] private float m_LateralWanderDistance = 0f;
		// how far the car will wander laterally towards its target
		[SerializeField] private float m_LateralWanderSpeed = 0f;
		// how fast the lateral wandering will fluctuate
		[SerializeField] [Range (0, 1)] private float m_AccelWanderAmount = 0.0f;
		// how much the cars acceleration will wander
		[SerializeField] private float m_AccelWanderSpeed = 0.0f;
		// how fast the cars acceleration wandering will fluctuate
		[SerializeField] private BrakeCondition m_BrakeCondition = BrakeCondition.TargetDirectionDifference;
		// what should the AI consider when accelerating/braking?
		[SerializeField] private bool m_Driving;
		// whether the AI is currently actively driving or stopped.
		[SerializeField] private bool m_StopWhenTargetReached;
		// should we stop driving when we reach the target?
		[SerializeField] private float m_ReachTargetThreshold = 2;
		// proximity to target to consider we 'reached' it, and stop driving.
		[SerializeField] private WaypointCircuit circuit;
		// A reference to the waypoint-based route we should follow
		[SerializeField] private bool m_isCircuit = false;
		// Is this a user-AI or just an AI car?
		[SerializeField] private bool m_isUser = false;

		private float m_RandomPerlin;
		// A random value for the car to base its wander on (so that AI cars don't all wander in the same pattern)
		private CarController m_CarController;
		// Reference to actual car controller we are controlling
		private float m_AvoidOtherCarTime;
		// time until which to avoid the car we recently collided with
		private float m_AvoidOtherCarSlowdown;
		// how much to slow down due to colliding with another car, whilst avoiding
		private float m_AvoidPathOffset = 0;
		// direction (-1 or 1) in which to offset path to avoid other car, whilst avoiding
		private Rigidbody m_Rigidbody;
		private Transform m_Target;
		private int progressNum;
		private VisualSteeringWheelController m_SteeringWheel;
		//SteeringWheelController
		private bool m_isPassing;
        // should be set to true if the car is passing
        private bool m_tooFar;
        //should be set when the car is too far from the vehicle it is following. Auto sets TooClose to false, if true.
        private bool m_tooClose;
        //should be set when the car is too close to the vehicle it is following. Auto sets TooFar to false, if true;
        private float m_accelMultiplier = 1;
		private Sensors m_Sensors;
		private SensorResponseHandler[] m_sensorResponseHandlers;
		// for avoiding obstacles via steer updates
		private bool m_isAvoidingObstacle;
		private float m_obstacleAvoidanceSteerAmount;


		/* Awake */

		private void Awake ()
		{
			m_CarController = GetComponent<CarController> ();
			m_Rigidbody = GetComponent<Rigidbody> ();
			m_SteeringWheel = GetComponentInChildren<VisualSteeringWheelController> ();
			m_Sensors = GetComponent<Sensors> ();
			m_sensorResponseHandlers = GetComponents<SensorResponseHandler> ();
			IsAvoidingObstacle = false;
			ObstacleAvoidanceSteerAmount = 1f;

            // give the random perlin a random value
            m_RandomPerlin = Random.value * 100;

			if (m_isUser) {
				IsUser = true;
			}

			progressNum = 0;
			SetTarget (circuit.Waypoints [progressNum], false);
		}

		private void onEnable ()
		{
			//When switched to AIControl, constrict steering angle
			if (m_isUser) {
				m_CarController.MaxSteeringAngle = 30f;
			} else {
				m_CarController.MaxSteeringAngle = 38f;
			}
		}

		private void FixedUpdate ()
		{
			if (m_Target == null || !m_Driving) {
				// Car should not be moving,
				// use handbrake to stop
				m_CarController.Move (0, 0, -1f, 1f);
			} else {
				
				/* SENSORS HERE */
				if (m_isUser) {
					Dictionary<int, VRAVEObstacle> vo;
					m_Sensors.Scan (out vo);
					foreach (SensorResponseHandler s in m_sensorResponseHandlers) {
						s.handle (this, vo, m_CarController.CurrentSpeed, m_BrakeCondition);
					}

					/* End sensors */
				}

				Vector3 fwd = transform.forward;
				if (m_Rigidbody.velocity.magnitude > m_CarController.MaxSpeed * 0.1f) {
					fwd = m_Rigidbody.velocity;
				}

				float desiredSpeed = m_CarController.MaxSpeed;

				// now it's time to decide if we should be slowing down...
				switch (m_BrakeCondition) {
				case BrakeCondition.TargetDirectionDifference:
					{
						// the car will brake according to the upcoming change in direction of the target. Useful for route-based AI, slowing for corners.

						// check out the angle of our target compared to the current direction of the car
						float approachingCornerAngle = Vector3.Angle (m_Target.forward, fwd);
						//Debug.Log("Approaching Corner Angle: " + approachingCornerAngle);
						// also consider the current amount we're turning, multiplied up and then compared in the same way as an upcoming corner angle
						float spinningAngle = m_Rigidbody.angularVelocity.magnitude * m_CautiousAngularVelocityFactor;
						//Debug.Log("Spinning Angle: " + spinningAngle);

						// if it's different to our current angle, we need to be cautious (i.e. slow down) a certain amount
						float cautiousnessRequired = Mathf.InverseLerp (0, m_CautiousMaxAngle,
							                             Mathf.Max (spinningAngle,
								                             approachingCornerAngle));
						//Debug.Log("Cautiousness Required: " + cautiousnessRequired);
						desiredSpeed = Mathf.Lerp (m_CarController.MaxSpeed, m_CarController.MaxSpeed * m_CautiousSpeedFactor,
							cautiousnessRequired);
						//Debug.Log("Approaching Corner Angle\tSpinningAngle" + approachingCornerAngle"DesiredSpeed: " + desiredSpeed);
						break;
					}

				case BrakeCondition.TargetDistance:
					{
						// the car will brake as it approaches its target, regardless of the target's direction. Useful if you want the car to
						// head for a stationary target and come to rest when it arrives there.

						// check out the distance to target
						Vector3 delta = m_Target.position - transform.position;
						float distanceCautiousFactor = Mathf.InverseLerp (m_CautiousMaxDistance, 0, delta.magnitude);

						// also consider the current amount we're turning, multiplied up and then compared in the same way as an upcoming corner angle
						float spinningAngle = m_Rigidbody.angularVelocity.magnitude * m_CautiousAngularVelocityFactor;

						// if it's different to our current angle, we need to be cautious (i.e. slow down) a certain amount
						float cautiousnessRequired = Mathf.Max (
							                             Mathf.InverseLerp (0, m_CautiousMaxAngle, spinningAngle), distanceCautiousFactor);
						desiredSpeed = Mathf.Lerp (m_CarController.MaxSpeed, m_CarController.MaxSpeed * m_CautiousSpeedFactor,
							cautiousnessRequired);
						break;
					}

				case BrakeCondition.NeverBrake:
					break;
				}

				// Evasive action due to collision with other cars:

				// our target position starts off as the 'real' target position
				Vector3 offsetTargetPos = m_Target.position;

				// if are we currently taking evasive action to prevent being stuck against another car:
				if (Time.time < m_AvoidOtherCarTime) {
					// slow down if necessary (if we were behind the other car when collision occured)
					desiredSpeed *= m_AvoidOtherCarSlowdown;

					// and veer towards the side of our path-to-target that is away from the other car
					offsetTargetPos += m_Target.right * m_AvoidPathOffset;
				} else {
					// no need for evasive action, we can just wander across the path-to-target in a random way,
					// which can help prevent AI from seeming too uniform and robotic in their driving
					offsetTargetPos += m_Target.right *
					(Mathf.PerlinNoise (Time.time * m_LateralWanderSpeed, m_RandomPerlin) * 2 - 1) *
					m_LateralWanderDistance;
				}

				// use different sensitivity depending on whether accelerating or braking:
				float accelBrakeSensitivity = (desiredSpeed < m_CarController.CurrentSpeed)
					? m_BrakeSensitivity
					: m_AccelSensitivity;

				// decide the actual amount of accel/brake input to achieve desired speed.
				float accel = Mathf.Clamp ((desiredSpeed - m_CarController.CurrentSpeed) * accelBrakeSensitivity, -1, 1);

				// add acceleration 'wander', which also prevents AI from seeming too uniform and robotic in their driving
				// i.e. increasing the accel wander amount can introduce jostling and bumps between AI cars in a race
				accel *= (1 - m_AccelWanderAmount) +
				(Mathf.PerlinNoise (Time.time * m_AccelWanderSpeed, m_RandomPerlin) * m_AccelWanderAmount);

				// calculate the local-relative position of the target, to steer towards
				Vector3 localTarget = transform.InverseTransformPoint (offsetTargetPos);

				// work out the local angle towards the target
				float targetAngle = Mathf.Atan2 (localTarget.x, localTarget.z) * Mathf.Rad2Deg;

				float steer = 0f;

				if (!IsAvoidingObstacle) {
					// get the amount of steering needed to aim the car towards the target
					steer = Mathf.Clamp (targetAngle * m_SteerSensitivity, -1, 1) * Mathf.Sign (m_CarController.CurrentSpeed);
				} else {
					steer = Mathf.Clamp (ObstacleAvoidanceSteerAmount * m_SteerSensitivity, -1, 1) * Mathf.Sign (m_CarController.CurrentSpeed);
					accel = accel * 10f;
				}

                /*
                // FOLLOWING BEHAVIOR ONLY
                Mathf.Clamp(AccelMultiplier, -10, 10);
                if(TooClose)
                {
                    //m_CarController.MaxSpeed = Time.deltaTime * m_CarController.MaxSpeed;
                    accel = -1*accel - m_accelMultiplier*(accel*Time.deltaTime);
                    Debug.Log("Too Close Accel : " + accel + "  MaxSpeed: " + m_CarController.MaxSpeed);
                    
                }
                else if(TooFar)
                {
                    //m_CarController.MaxSpeed = Time.deltaTime * m_CarController.MaxSpeed;
                    accel = accel + m_accelMultiplier*(accel*Time.deltaTime);
                    Debug.Log("Too Far Accel : " + accel + "  MaxSpeed: " + m_CarController.MaxSpeed);
                }
                */
               
				m_CarController.Move (steer, accel, accel, 0f);

				if (m_isUser) {
					// turn the steering wheel 
					m_SteeringWheel.turnSteeringWheel ((float)steer, m_CarController.CurrentSteerAngle);
				}

				// if appropriate, stop driving when we're close enough to the target.
				if (m_StopWhenTargetReached && localTarget.magnitude < m_ReachTargetThreshold) {
					m_Driving = false;
				} else if (!m_StopWhenTargetReached && localTarget.magnitude < m_ReachTargetThreshold) {
					if (m_isCircuit) {

						SetTarget (circuit.Waypoints [++progressNum % circuit.Waypoints.Length], false);
					} else {
						if (progressNum == circuit.Waypoints.Length - 1) {
							m_StopWhenTargetReached = true;
						} else {
							SetTarget (circuit.Waypoints [++progressNum], false);
						}
						
					}
				}

			}
		}


		//private void OnCollisionStay (Collision col)
		//{
		//	// detect collision against other cars, so that we can take evasive action
		//	if (col.rigidbody != null) {
		//		var otherAI = col.rigidbody.GetComponent<CarAIControl> ();
		//		if (otherAI != null) {
		//			// we'll take evasive action for 1 second
		//			m_AvoidOtherCarTime = Time.time + 1;

		//			// but who's in front?...
		//			if (Vector3.Angle (transform.forward, otherAI.transform.position - transform.position) < 90) {
		//				// the other ai is in front, so it is only good manners that we ought to brake...
		//				m_AvoidOtherCarSlowdown = 0.5f;
		//			} else {
		//				// we're in front! ain't slowing down for anybody...
		//				m_AvoidOtherCarSlowdown = 1;
		//			}

		//			// both cars should take evasive action by driving along an offset from the path centre,
		//			// away from the other car
		//			var otherCarLocalDelta = transform.InverseTransformPoint (otherAI.transform.position);
		//			float otherCarAngle = Mathf.Atan2 (otherCarLocalDelta.x, otherCarLocalDelta.z);
		//			m_AvoidPathOffset = m_LateralWanderDistance * -Mathf.Sign (otherCarAngle);
		//		}
		//	}
		//}

		public void SetTarget (Transform target, bool stopWhenTargetReached)
		{
			m_Target = target;
			m_Driving = true;
			m_StopWhenTargetReached = stopWhenTargetReached;

			// set this really high to ensure the car doesn't collide with the obstacle
			if (m_StopWhenTargetReached) {
				m_ReachTargetThreshold = 15f;
			}
		}
			
		//Circuit Handling and get/set functions

		/*Use this function if you want to choose the starting point of the circuit.*/
		public void switchCircuit (WaypointCircuit c, int progress)
		{
            circuit = c;
            ProgressNum = progress;
            SetTarget(circuit.Waypoints[ProgressNum], false);
        }


        /*Use this function just to set a new Circuit and start at the beginning*/

        public WaypointCircuit Circuit {
			get {
				return circuit;
			}

			set {
				circuit = value;
				progressNum = 0;
				SetTarget (circuit.Waypoints [progressNum], false);
			}
		}

		public bool IsCircuit {
			get {
				return m_isCircuit;
			}

			set {
				m_isCircuit = value;
			}
		}

		public int ProgressNum {
			get {
				return progressNum;
			}

			set {
				progressNum = value;
			}
		}

		public Transform Target {
			get {
				return m_Target;
			}

			set {
				m_Target = value;
			}
		}

		public bool IsUser {
			set {
				if (value) {
					if (m_SteeringWheel == null) {
						m_SteeringWheel = GetComponentInChildren<VisualSteeringWheelController> ();
					} 
				}
				m_isUser = value;
			}
			get { return m_isUser; }
		}

		public bool IsPassing { get { return m_isPassing; } set { m_isPassing = value; } }

		public BrakeCondition AIBrakeCondition {
			get {
				return m_BrakeCondition;
			}

			set {
				m_BrakeCondition = value;
			}
		}

		public bool StopWhenTargetReached {
			get {
				return m_StopWhenTargetReached;
			}

			set {
				m_StopWhenTargetReached = value;
			}
		}

		public float ReachTargetThreshold {
			get {
				return m_ReachTargetThreshold;
			}

			set {
				m_ReachTargetThreshold = value;
			}
		}



		//AI cautiousness settings getters and setters
		public float CautiousSpeedFactor {
			get {
				return m_CautiousSpeedFactor;
			}

			set {
				m_CautiousSpeedFactor = value;
			}
		}

		public float CautiousMaxAngle {
			get {
				return m_CautiousMaxAngle;
			}

			set {
				m_CautiousMaxAngle = value;
			}
		}

		public float CautiousMaxDistance {
			get {
				return m_CautiousMaxDistance;
			}

			set {
				m_CautiousMaxDistance = value;
			}
		}

		public float CautiousAngularVelocityFactor {
			get {
				return m_CautiousAngularVelocityFactor;
			}

			set {
				m_CautiousAngularVelocityFactor = value;
			}
		}

		public float SteerSensitivity {
			get {
				return m_SteerSensitivity;
			}

			set {
				m_SteerSensitivity = value;
			}
		}

		public float AccelSensitivity {
			get {
				return m_AccelSensitivity;
			}

			set {
				m_AccelSensitivity = value;
			}
		}

		public float BrakeSensitivity {
			get {
				return m_BrakeSensitivity;
			}

			set {
				m_BrakeSensitivity = value;
			}
		}

        public bool TooFar
        {
            get
            {
                return m_tooFar;
            }

            set
            {
                m_tooFar = value;
                if (value == true)
                {
                    m_tooFar = false;
                }
            }
        }

        public bool TooClose
        {
            get
            {
                return m_tooClose;
            }

            set
            {
                m_tooClose = value;
                if(value == true)
                {
                    m_tooFar = false;
                }
            }
        }

        public float AccelMultiplier
        {
            get
            {
                return m_accelMultiplier;
            }

            set
            {
                m_accelMultiplier = value;
            }
        }

        public float AvoidOtherCarSlowdown
        {
            get
            {
                return m_AvoidOtherCarSlowdown;
            }

            set
            {
                m_AvoidOtherCarSlowdown = value;
            }
        }

        public float AvoidOtherCarTime
        {
            get
            {
                return m_AvoidOtherCarTime;
            }

            set
            {
                m_AvoidOtherCarTime = value;
            }
        }

		public bool Driving {
			get {
				return m_Driving;
			}
			set {
				m_Driving = value;
			}
		}

		public bool IsAvoidingObstacle {
			get {
				return m_isAvoidingObstacle;
			}
			set { m_isAvoidingObstacle = value;}
		}

		public float ObstacleAvoidanceSteerAmount {
			get {
				return m_obstacleAvoidanceSteerAmount;
			}
			set {
				m_obstacleAvoidanceSteerAmount = value;
			}
		}
	}

}
