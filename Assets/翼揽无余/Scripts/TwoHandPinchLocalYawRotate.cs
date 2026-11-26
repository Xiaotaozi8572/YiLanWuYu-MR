using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Hands;
using UnityEngine.XR.Interaction.Toolkit;

/// <summary>
/// 仅双手同时捏合激活交互；平均位移、双手连线角度和手距驱动旋转及缩放。
/// 不依赖飞机本体的 Collider 或 XR Grab Interactable。
/// </summary>
public sealed class TwoHandPinchLocalYawRotate : MonoBehaviour
{
    public enum RotationMode { YAxisOnly, FreeRotation }

    [Header("旋转对象")]
    [Tooltip("拖入 AircraftRotationRoot。")]
    [SerializeField] private Transform rotationTarget;
    [Tooltip("可选；留空时以模型根节点为中心。指定时应使用不随模型旋转的 Transform。")]
    [SerializeField] private Transform rotationCenter;
    [SerializeField] private RotationMode rotationMode = RotationMode.YAxisOnly;

    [Header("捏合识别（米）")]
    [Tooltip("拇指尖与食指尖距离小于该值时开始捏合。")]
    [Min(0.005f)] [SerializeField] private float pinchEnterDistance = 0.025f;

    [Tooltip("距离大于该值时判定松手。应略大于开始捏合距离，以减少抖动。")]
    [Min(0.01f)] [SerializeField] private float pinchExitDistance = 0.04f;

    [Header("旋转手感")]
    [Tooltip("双手捏合点的平均位置每移动一米对应的旋转角度。")]
    [Min(1f)] [SerializeField] private float rotationSensitivity = 180f;
    [Min(0f)] [SerializeField] private float twoHandTwistSensitivity = 1f;
    [Min(0f)] [SerializeField] private float pitchSensitivity = 120f;
    [Tooltip("双手改变间距时缩放模型。")]
    [SerializeField] private bool enableTwoHandScaling;
    [Tooltip("开启双手缩放后，每改变一米手距对应的缩放比例。")]
    [Min(0f)] [SerializeField] private float scaleSensitivity = 1f;
    [Range(0f, 0.02f)] [SerializeField] private float movementDeadZone = 0.002f;

    [Tooltip("如果飞机旋转方向与双手动作相反，勾选此项。")]
    [SerializeField] private bool invertRotationDirection;

    [Tooltip("忽略小于该角度的手部抖动。")]
    [Range(0f, 3f)] [SerializeField] private float angleDeadZone = 0.15f;

    [Tooltip("限制单帧最大旋转角度，防止追踪跳变导致飞机突然旋转。")]
    [Range(1f, 30f)] [SerializeField] private float maxDegreesPerFrame = 12f;

    [Tooltip("数值越大越平滑，但跟手性会稍弱。")]
    [Range(0f, 0.3f)] [SerializeField] private float smoothTime = 0.035f;

    [Header("旋转期间暂停热点")]
    [Tooltip("可拖入各热点上的 XR Simple Interactable，旋转时会暂时禁止点击。")]
    [SerializeField] private XRBaseInteractable[] hotspotInteractables;

    [Tooltip("松手后延迟恢复热点，避免松手瞬间误点。")]
    [Range(0f, 1f)] [SerializeField] private float hotspotRestoreDelay = 0.25f;

    private readonly List<XRHandSubsystem> handSubsystems = new List<XRHandSubsystem>();
    private XRHandSubsystem handSubsystem;

    private bool leftPinching;
    private bool rightPinching;
    private int activeHands;
    private bool rotationBlocked;

    private Vector3 previousCenter, previousHandLine, gestureRight, gestureForward;
    private Vector3 pivot, startOffset;
    private Quaternion startRotation;
    private float desiredYaw, desiredPitch, appliedYaw, appliedPitch;
    private float yawVelocity, pitchVelocity;
    private float scaleStartDistance;
    private Vector3 scaleStartValue;
    private Coroutine restoreHotspotsCoroutine;

    private void OnValidate()
    {
        if (pinchExitDistance <= pinchEnterDistance)
        {
            pinchExitDistance = pinchEnterDistance + 0.01f;
        }
    }

    private void Update()
    {
        if (rotationTarget == null) return;
        if (rotationBlocked || !TryGetRunningHandSubsystem())
        {
            EndRotation();
            return;
        }

        bool leftTracked = TryReadHand(handSubsystem.leftHand, leftPinching, out Vector3 left, out leftPinching);
        bool rightTracked = TryReadHand(handSubsystem.rightHand, rightPinching, out Vector3 right, out rightPinching);
        if (!leftTracked || !rightTracked || !leftPinching || !rightPinching)
        {
            EndRotation();
            return;
        }

        Vector3 center = (left + right) * 0.5f;
        Vector3 line = right - left;
        if (activeHands == 0)
        {
            BeginRotation();
            activeHands = 3;
            previousCenter = center;
            previousHandLine = line;
            scaleStartDistance = line.magnitude;
            scaleStartValue = rotationTarget.localScale;
            CaptureGestureAxes();
            return;
        }

        Vector3 movement = center - previousCenter;
        float horizontal = Vector3.Dot(movement, gestureRight) + Vector3.Dot(movement, gestureForward);
        float vertical = Vector3.Dot(movement, Vector3.up);
        float yawDelta = 0f;
        float pitchDelta = 0f;
        if (Mathf.Abs(horizontal) >= movementDeadZone)
        {
            yawDelta = horizontal * rotationSensitivity;
            previousCenter += gestureRight * Vector3.Dot(movement, gestureRight)
                + gestureForward * Vector3.Dot(movement, gestureForward);
        }
        if (rotationMode == RotationMode.FreeRotation && Mathf.Abs(vertical) >= movementDeadZone)
        {
            pitchDelta = -vertical * pitchSensitivity;
            previousCenter += Vector3.up * vertical;
        }

        Vector3 oldLine = Vector3.ProjectOnPlane(previousHandLine, Vector3.up);
        Vector3 newLine = Vector3.ProjectOnPlane(line, Vector3.up);
        if (oldLine.sqrMagnitude > 0.0025f && newLine.sqrMagnitude > 0.0025f)
        {
            float twist = Vector3.SignedAngle(oldLine, newLine, Vector3.up);
            if (Mathf.Abs(twist) >= angleDeadZone)
            {
                yawDelta += twist * twoHandTwistSensitivity;
                previousHandLine = line;
            }
        }
        else previousHandLine = line;

        float direction = invertRotationDirection ? -1f : 1f;
        desiredYaw += Mathf.Clamp(yawDelta * direction, -maxDegreesPerFrame, maxDegreesPerFrame);
        desiredPitch += Mathf.Clamp(pitchDelta * direction, -maxDegreesPerFrame, maxDegreesPerFrame);
        ApplyRotation();
        if (enableTwoHandScaling && scaleStartDistance > 0.05f)
        {
            float factor = Mathf.Clamp(1f + (line.magnitude - scaleStartDistance) * scaleSensitivity, 0.25f, 4f);
            Vector3 targetScale = scaleStartValue * factor;
            float blend = smoothTime <= 0f ? 1f : 1f - Mathf.Exp(-Time.deltaTime / smoothTime);
            rotationTarget.localScale = Vector3.Lerp(rotationTarget.localScale, targetScale, blend);
        }
    }

    /// <summary>
    /// 标签打开时可通过 Button 事件传入 true，关闭标签时传入 false。
    /// </summary>
    public void SetRotationBlocked(bool blocked)
    {
        rotationBlocked = blocked;
        if (blocked)
        {
            EndRotation();
        }
    }

    private bool TryGetRunningHandSubsystem()
    {
        if (handSubsystem != null && handSubsystem.running)
        {
            return true;
        }

        handSubsystems.Clear();
        SubsystemManager.GetSubsystems(handSubsystems);

        for (int i = 0; i < handSubsystems.Count; i++)
        {
            XRHandSubsystem candidate = handSubsystems[i];
            if (candidate != null && candidate.running)
            {
                handSubsystem = candidate;
                return true;
            }
        }

        handSubsystem = null;
        return false;
    }

    private bool TryReadHand(
        XRHand hand,
        bool wasPinching,
        out Vector3 palmPosition,
        out bool isPinching)
    {
        palmPosition = default;
        isPinching = false;

        if (!hand.isTracked)
        {
            return false;
        }

        XRHandJoint thumbTip = hand.GetJoint(XRHandJointID.ThumbTip);
        XRHandJoint indexTip = hand.GetJoint(XRHandJointID.IndexTip);

        if (!thumbTip.TryGetPose(out Pose thumbPose) ||
            !indexTip.TryGetPose(out Pose indexPose))
        {
            return false;
        }

        float pinchDistance = Vector3.Distance(thumbPose.position, indexPose.position);
        isPinching = wasPinching
            ? pinchDistance < pinchExitDistance
            : pinchDistance < pinchEnterDistance;

        palmPosition = (thumbPose.position + indexPose.position) * 0.5f;
        return true;
    }

    private void BeginRotation()
    {
        startRotation = rotationTarget.rotation;
        pivot = rotationCenter != null ? rotationCenter.position : rotationTarget.position;
        startOffset = rotationTarget.position - pivot;
        desiredYaw = desiredPitch = appliedYaw = appliedPitch = yawVelocity = pitchVelocity = 0f;

        if (restoreHotspotsCoroutine != null)
        {
            StopCoroutine(restoreHotspotsCoroutine);
            restoreHotspotsCoroutine = null;
        }

        SetHotspotsEnabled(false);
    }

    private void EndRotation()
    {
        if (activeHands == 0) return;
        activeHands = 0;
        desiredYaw = appliedYaw;
        desiredPitch = appliedPitch;
        yawVelocity = pitchVelocity = 0f;

        if (restoreHotspotsCoroutine != null)
        {
            StopCoroutine(restoreHotspotsCoroutine);
        }

        restoreHotspotsCoroutine = StartCoroutine(RestoreHotspotsAfterDelay());
    }

    private IEnumerator RestoreHotspotsAfterDelay()
    {
        if (hotspotRestoreDelay > 0f)
        {
            yield return new WaitForSeconds(hotspotRestoreDelay);
        }

        SetHotspotsEnabled(true);
        restoreHotspotsCoroutine = null;
    }

    private void SetHotspotsEnabled(bool enabled)
    {
        if (hotspotInteractables == null)
        {
            return;
        }

        for (int i = 0; i < hotspotInteractables.Length; i++)
        {
            if (hotspotInteractables[i] != null)
            {
                hotspotInteractables[i].enabled = enabled;
            }
        }
    }

    private void CaptureGestureAxes()
    {
        Transform head = Camera.main != null ? Camera.main.transform : null;
        gestureForward = head != null ? Vector3.ProjectOnPlane(head.forward, Vector3.up) : Vector3.forward;
        if (gestureForward.sqrMagnitude < 0.01f) gestureForward = Vector3.forward;
        gestureForward.Normalize();
        gestureRight = Vector3.Cross(Vector3.up, gestureForward).normalized;
    }

    private void ApplyRotation()
    {
        if (smoothTime <= 0f)
        {
            appliedYaw = desiredYaw;
            appliedPitch = desiredPitch;
        }
        else
        {
            appliedYaw = Mathf.SmoothDamp(appliedYaw, desiredYaw, ref yawVelocity, smoothTime);
            appliedPitch = Mathf.SmoothDamp(appliedPitch, desiredPitch, ref pitchVelocity, smoothTime);
        }
        Quaternion delta = Quaternion.AngleAxis(appliedYaw, Vector3.up);
        if (rotationMode == RotationMode.FreeRotation)
            delta *= Quaternion.AngleAxis(appliedPitch, gestureRight);
        rotationTarget.rotation = delta * startRotation;
        rotationTarget.position = pivot + delta * startOffset;
    }

    private void OnDisable()
    {
        activeHands = 0;
        leftPinching = rightPinching = false;
        if (restoreHotspotsCoroutine != null) StopCoroutine(restoreHotspotsCoroutine);
        restoreHotspotsCoroutine = null;
        SetHotspotsEnabled(true);
    }
}
