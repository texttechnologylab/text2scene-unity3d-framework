﻿using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.Assertions;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.UI;

/// <summary>
/// Interactor used for interacting with interactables at a distance. This is handled via raycasts
/// that update the current set of valid targets for this interactor. Allows scaling in addition to moving and rotating.
/// </summary>
[DisallowMultipleComponent]
[AddComponentMenu("XR/XR Edit Interactor")]
[HelpURL(XRHelpURLConstants.k_XRRayInteractor)]
public class XREditInteractor : XRBaseControllerInteractor, ILineRenderable, IUIInteractor
{
    /// <summary>
    /// Compares raycast hits by distance, to sort in ascending order.
    /// </summary>
    protected sealed class RaycastHitComparer : IComparer<RaycastHit>
    {
        /// <summary>
        /// Compares raycast hits by distance in ascending order.
        /// </summary>
        /// <param name="a">The first raycast hit to compare.</param>
        /// <param name="b">The second raycast hit to compare.</param>
        /// <returns>Returns less than 0 if a is closer than b. 0 if a and b are equal. Greater than 0 if b is closer than a.</returns>
        public int Compare(RaycastHit a, RaycastHit b)
        {
            var aDistance = a.collider != null ? a.distance : float.MaxValue;
            var bDistance = b.collider != null ? b.distance : float.MaxValue;
            return aDistance.CompareTo(bDistance);
        }
    }

    const int k_MaxRaycastHits = 10;

    const int k_MinSampleFrequency = 2;
    const int k_MaxSampleFrequency = 100;

    /// <summary>
    /// Sets which trajectory path to use for the cast when detecting collisions.
    /// </summary>
    /// <seealso cref="lineType"/>
    public enum LineType
    {
        /// <summary>
        /// Performs a single raycast into the Scene with a set ray length.
        /// </summary>
        StraightLine,

        /// <summary>
        /// Samples the trajectory of a projectile to generate a projectile curve.
        /// </summary>
        ProjectileCurve,

        /// <summary>
        /// Uses a control point and an end point to create a quadratic Bézier curve.
        /// </summary>
        BezierCurve,
    }

    /// <summary>
    /// Sets which shape of physics cast to use for the cast when detecting collisions.
    /// </summary>
    public enum HitDetectionType
    {
        /// <summary>
        /// Uses <see cref="Physics"/> Raycast to detect collisions.
        /// </summary>
        Raycast,

        /// <summary>
        /// Uses <see cref="Physics"/> Sphere Cast to detect collisions.
        /// </summary>
        SphereCast,
    }

    public enum AnchorControlMode
    {
        Translate,
        Scale,
        RotateX,
        RotateY,
        RotateZ,
    }


    [SerializeField]
    LineType m_LineType = LineType.StraightLine;
    /// <summary>
    /// Gets or sets the type of ray cast.
    /// </summary>
    public LineType lineType
    {
        get => m_LineType;
        set => m_LineType = value;
    }

    [SerializeField]
    bool m_BlendVisualLinePoints = true;
    /// <summary>
    /// Blend the line sample points used for raycasting with the current pose of the controller.
    /// Use this to make the line visual stay connected with the controller instead of lagging behind.
    /// </summary>
    /// <remarks>
    /// When the controller is configured to sample tracking input directly before rendering to reduce
    /// input latency, the controller may be in a new position or rotation relative to the starting point
    /// of the sample curve used for raycasting.
    /// <br/>
    /// A value of <see langword="false"/> will make the line visual stay at a fixed reference frame rather than bending
    /// or curving towards the end of the raycast line.
    /// </remarks>
    public bool blendVisualLinePoints
    {
        get => m_BlendVisualLinePoints;
        set => m_BlendVisualLinePoints = value;
    }

    [SerializeField]
    float m_MaxRaycastDistance = 30f;
    /// <summary>
    /// Gets or sets the max distance of ray cast when the line type is a straight line.
    /// Increasing this value will make the line reach further.
    /// </summary>
    /// <seealso cref="LineType.StraightLine"/>
    public float maxRaycastDistance
    {
        get => m_MaxRaycastDistance;
        set => m_MaxRaycastDistance = value;
    }

    [SerializeField]
    Transform m_ReferenceFrame;
    /// <summary>
    /// The reference frame of the curve to define the ground plane and up.
    /// If not set at startup it will try to find the <see cref="XRRig.rig"/> GameObject,
    /// and if that does not exist it will use global up and origin by default.
    /// </summary>
    /// <seealso cref="LineType.ProjectileCurve"/>
    /// <seealso cref="LineType.BezierCurve"/>
    public Transform referenceFrame
    {
        get => m_ReferenceFrame;
        set => m_ReferenceFrame = value;
    }

    [SerializeField]
    float m_Velocity = 16f;
    /// <summary>
    /// Initial velocity of the projectile. Increasing this value will make the curve reach further.
    /// </summary>
    /// <seealso cref="LineType.ProjectileCurve"/>
    public float velocity
    {
        get => m_Velocity;
        set => m_Velocity = value;
    }

    /// <summary>
    /// Initial velocity of the projectile. Increasing this value will make the curve reach further.
    /// </summary>
    /// <seealso cref="LineType.ProjectileCurve"/>
#pragma warning disable IDE1006 // Naming Styles
    [Obsolete("Velocity has been deprecated. Use velocity instead. (UnityUpgradable) -> velocity")]
    public float Velocity
    {
        get => velocity;
        set => velocity = value;
    }
#pragma warning restore IDE1006

    [SerializeField]
    float m_Acceleration = 9.8f;
    /// <summary>
    /// Gravity of the projectile in the reference frame.
    /// </summary>
    /// <seealso cref="LineType.ProjectileCurve"/>
    public float acceleration
    {
        get => m_Acceleration;
        set => m_Acceleration = value;
    }

    /// <summary>
    /// Gravity of the projectile in the reference frame.
    /// </summary>
    /// <seealso cref="LineType.ProjectileCurve"/>
#pragma warning disable IDE1006 // Naming Styles
    [Obsolete("Acceleration has been deprecated. Use acceleration instead. (UnityUpgradable) -> acceleration")]
    public float Acceleration
    {
        get => acceleration;
        set => acceleration = value;
    }
#pragma warning restore IDE1006

    [SerializeField]
    float m_AdditionalGroundHeight = 0.1f;
    /// <summary>
    /// Additional height below ground level that the projectile will continue to.
    /// Increasing this value will make the end point drop lower in height.
    /// </summary>
    /// <seealso cref="LineType.ProjectileCurve"/>
    public float additionalGroundHeight
    {
        get => m_AdditionalGroundHeight;
        set => m_AdditionalGroundHeight = value;
    }

    [SerializeField]
    float m_AdditionalFlightTime = 0.5f;
    /// <summary>
    /// Additional flight time after the projectile lands at the adjusted ground level.
    /// Increasing this value will make the end point drop lower in height.
    /// </summary>
    /// <seealso cref="LineType.ProjectileCurve"/>
    public float additionalFlightTime
    {
        get => m_AdditionalFlightTime;
        set => m_AdditionalFlightTime = value;
    }

    /// <inheritdoc cref="additionalFlightTime"/>
#pragma warning disable IDE1006 // Naming Styles
    [Obsolete("AdditionalFlightTime has been deprecated. Use additionalFlightTime instead. (UnityUpgradable) -> additionalFlightTime")]
    public float AdditionalFlightTime
    {
        get => additionalFlightTime;
        set => additionalFlightTime = value;
    }
#pragma warning restore IDE1006

    [SerializeField]
    float m_EndPointDistance = 30f;
    /// <summary>
    /// Increase this value distance will make the end of curve further from the start point.
    /// </summary>
    /// <seealso cref="LineType.BezierCurve"/>
    public float endPointDistance
    {
        get => m_EndPointDistance;
        set => m_EndPointDistance = value;
    }

    [SerializeField]
    float m_EndPointHeight = -10f;
    /// <summary>
    /// Decrease this value will make the end of the curve drop lower relative to the start point.
    /// </summary>
    /// <seealso cref="LineType.BezierCurve"/>
    public float endPointHeight
    {
        get => m_EndPointHeight;
        set => m_EndPointHeight = value;
    }

    [SerializeField]
    float m_ControlPointDistance = 10f;
    /// <summary>
    /// Increase this value will make the peak of the curve further from the start point.
    /// </summary>
    /// <seealso cref="LineType.BezierCurve"/>
    public float controlPointDistance
    {
        get => m_ControlPointDistance;
        set => m_ControlPointDistance = value;
    }

    [SerializeField]
    float m_ControlPointHeight = 5f;
    /// <summary>
    /// Increase this value will make the peak of the curve higher relative to the start point.
    /// </summary>
    /// <seealso cref="LineType.BezierCurve"/>
    public float controlPointHeight
    {
        get => m_ControlPointHeight;
        set => m_ControlPointHeight = value;
    }

    [SerializeField]
    [Range(k_MinSampleFrequency, k_MaxSampleFrequency)]
    int m_SampleFrequency = 20;
    /// <summary>
    /// The number of sample points used to approximate curved paths.
    /// Larger values produce a better quality approximate at the cost of reduced performance
    /// due to the number of raycasts.
    /// </summary>
    /// <remarks>
    /// A value of <i>n</i> will result in <i>n - 1</i> line segments for raycast.
    /// This property is not used when using <see cref="LineType.StraightLine"/> since the value would always be 2.
    /// </remarks>
    /// <seealso cref="LineType.ProjectileCurve"/>
    /// <seealso cref="LineType.BezierCurve"/>
    public int sampleFrequency
    {
        get => m_SampleFrequency;
        set => m_SampleFrequency = SanitizeSampleFrequency(value);
    }

    [SerializeField]
    HitDetectionType m_HitDetectionType = HitDetectionType.Raycast;
    /// <summary>
    /// Sets which type of hit detection to use for the raycast.
    /// </summary>
    public HitDetectionType hitDetectionType
    {
        get => m_HitDetectionType;
        set => m_HitDetectionType = value;
    }

    [SerializeField]
    [Range(0.01f, 0.25f)]
    float m_SphereCastRadius = 0.1f;
    /// <summary>
    /// Gets or sets radius used for sphere casting. Will use regular raycasting if set to 0 or less.
    /// </summary>
    /// <seealso cref="HitDetectionType.SphereCast"/>
    public float sphereCastRadius
    {
        get => m_SphereCastRadius;
        set => m_SphereCastRadius = value;
    }

    [SerializeField]
    LayerMask m_RaycastMask = -1;
    /// <summary>
    /// Gets or sets layer mask used for limiting raycast targets.
    /// </summary>
    public LayerMask raycastMask
    {
        get => m_RaycastMask;
        set => m_RaycastMask = value;
    }

    [SerializeField]
    QueryTriggerInteraction m_RaycastTriggerInteraction = QueryTriggerInteraction.Ignore;
    /// <summary>
    /// Gets or sets type of interaction with trigger volumes via raycast.
    /// </summary>
    public QueryTriggerInteraction raycastTriggerInteraction
    {
        get => m_RaycastTriggerInteraction;
        set => m_RaycastTriggerInteraction = value;
    }

    [SerializeField]
    bool m_HitClosestOnly;
    /// <summary>
    /// Consider only the closest Interactable as a valid target for interaction.
    /// </summary>
    /// <remarks>
    /// Enable this to make only the closest Interactable receive hover events.
    /// Otherwise, all hit Interactables will be considered valid and this Interactor will multi-hover.
    /// </remarks>
    /// <seealso cref="GetValidTargets"/>
    public bool hitClosestOnly
    {
        get => m_HitClosestOnly;
        set => m_HitClosestOnly = value;
    }

    [SerializeField]
    bool m_KeepSelectedTargetValid = true;
    /// <summary>
    /// Whether to keep selecting the target when not pointing to it after initially selecting it.
    /// It is recommended to set this value to <see langword="true"/> for grabbing objects, <see langword="false"/> for teleportation interactables.
    /// </summary>
    public bool keepSelectedTargetValid
    {
        get => m_KeepSelectedTargetValid;
        set => m_KeepSelectedTargetValid = value;
    }

    [SerializeField]
    bool m_HoverToSelect;
    /// <summary>
    /// Whether this Interactor will automatically select an Interactable after hovering over it for a period of time.
    /// </summary>
    /// <seealso cref="hoverTimeToSelect"/>
    public bool hoverToSelect
    {
        get => m_HoverToSelect;
        set => m_HoverToSelect = value;
    }

    [SerializeField]
    float m_HoverTimeToSelect = 0.5f;
    /// <summary>
    /// Number of seconds for which this Interactor must hover over an Interactable to select it if Hover To Select is enabled.
    /// </summary>
    /// <seealso cref="hoverToSelect"/>
    public float hoverTimeToSelect
    {
        get => m_HoverTimeToSelect;
        set => m_HoverTimeToSelect = value;
    }

    [SerializeField]
    bool m_EnableUIInteraction = true;
    /// <summary>
    /// Gets or sets whether this interactor is able to affect UI.
    /// </summary>
    public bool enableUIInteraction
    {
        get => m_EnableUIInteraction;
        set
        {
            if (m_EnableUIInteraction != value)
            {
                m_EnableUIInteraction = value;
                RegisterOrUnregisterXRUIInputModule();
            }
        }
    }

    [SerializeField]
    bool m_AllowAnchorControl = true;
    /// <summary>
    /// Allows the user to move the attach anchor point using the joystick.
    /// </summary>
    /// <seealso cref="anchorControlMode"/>
    /// <seealso cref="rotateSpeed"/>
    /// <seealso cref="translateSpeed"/>
    /// <seealso cref="scaleSpeed"/>
    /// <seealso cref="anchorRotateReferenceFrame"/>
    public bool allowAnchorControl
    {
        get => m_AllowAnchorControl;
        set => m_AllowAnchorControl = value;
    }

    [SerializeField]
    bool m_UseForceGrab = true;
    /// <summary>
    /// Force grab moves the object to your hand rather than interacting with it at a distance.
    /// </summary>
    public bool useForceGrab
    {
        get => m_UseForceGrab;
        set => m_UseForceGrab = value;
    }

    [SerializeField]
    AnchorControlMode m_AnchorControlMode = AnchorControlMode.Translate;
    /// <summary>
    /// The current Interaction mode of this interactor.
    /// </summary>
    public AnchorControlMode anchorControlMode
    {
        get => m_AnchorControlMode;
        set => m_AnchorControlMode = value;
    }

    [SerializeField]
    TMP_Text m_AnchorControlModeText;
    /// <summary>
    /// The TextMeshPro Component that should show this controllers anchor control mode as text.
    /// </summary>
    public TMP_Text anchorControlModeText
    {
        get => m_AnchorControlModeText;
        set => m_AnchorControlModeText = value;
    }


    [SerializeField]
    bool m_ResetAnchorControlMode = false;
    /// <summary>
    /// Whether to reset this interactors control mode on picking up a new interactable.
    /// </summary>
    public bool resetAnchorControlMode
    {
        get => m_ResetAnchorControlMode;
        set => m_ResetAnchorControlMode = value;
    }

    [SerializeField]
    float m_RotateSpeed = 180f;
    /// <summary>
    /// Speed that the anchor is rotated at.
    /// </summary>
    /// <seealso cref="allowAnchorControl"/>
    /// <seealso cref="translateSpeed"/>
    /// <seealso cref="scaleSpeed"/>
    public float rotateSpeed
    {
        get => m_RotateSpeed;
        set => m_RotateSpeed = value;
    }

    [SerializeField]
    float m_TranslateSpeed = 1f;
    /// <summary>
    /// Speed that the anchor is translated at.
    /// </summary>
    /// <seealso cref="allowAnchorControl"/>
    /// <seealso cref="rotateSpeed"/>
    /// <seealso cref="scaleSpeed"/>
    public float translateSpeed
    {
        get => m_TranslateSpeed;
        set => m_TranslateSpeed = value;
    }

    [SerializeField]
    float m_ScaleSpeed = 0.5f;
    /// <summary>
    /// Speed that the anchor is translated at.
    /// </summary>
    /// <seealso cref="allowAnchorControl"/>
    /// <seealso cref="rotateSpeed"/>
    /// <seealso cref="translateSpeed"/>
    public float scaleSpeed
    {
        get => m_ScaleSpeed;
        set => m_ScaleSpeed = value;
    }

    [SerializeField]
    Transform m_AnchorRotateReferenceFrame;
    /// <summary>
    /// The optional reference frame to define the up, forward, r ight axis when rotating the attach anchor point.
    /// When not set, rotates about the local axis of the attach transform.
    /// </summary>
    /// <seealso cref="allowAnchorControl"/>
    /// <seealso cref="RotateAnchorX"/>
    /// <seealso cref="RotateAnchorY"/>
    /// <seealso cref="RotateAnchorZ"/>
    public Transform anchorRotateReferenceFrame
    {
        get => m_AnchorRotateReferenceFrame;
        set => m_AnchorRotateReferenceFrame = value;
    }

    /// <summary>
    /// The launch angle of the Projectile Curve.
    /// More specifically, this is the signed angle in degrees between the original attach forward
    /// direction and the plane of the reference frame, with positive angles when pointing upward.
    /// </summary>
    public float angle
    {
        get
        {
            var castForward = startTransform.forward;
            var up = m_ReferenceFrame != null ? m_ReferenceFrame.up : Vector3.up;
            var projectedForward = Vector3.ProjectOnPlane(castForward, up);
            return Mathf.Approximately(Vector3.Angle(castForward, projectedForward), 0f)
                ? 0f
                : Vector3.SignedAngle(castForward, projectedForward, Vector3.Cross(up, castForward));
        }
    }

    /// <inheritdoc cref="angle"/>
#pragma warning disable IDE1006 // Naming Styles
    [Obsolete("Angle has been deprecated. Use angle instead. (UnityUpgradable) -> angle")]
    public float Angle => angle;
#pragma warning restore IDE1006

    readonly List<XRBaseInteractable> m_ValidTargets = new List<XRBaseInteractable>();
    /// <inheritdoc />
    protected override List<XRBaseInteractable> validTargets => m_ValidTargets;

    Transform m_OriginalAttachTransform;
    /// <summary>
    /// The <see cref="Transform"/> that upon entering selection
    /// (when this interactor first initiates selection of an interactable),
    /// this interactor will copy the pose of the attach <see cref="Transform"/> values into.
    /// </summary>
    /// <remarks>
    /// Automatically instantiated and set in <see cref="Awake"/>.
    /// Setting this will not automatically destroy the previous object.
    /// </remarks>
    /// <seealso cref="XRBaseInteractor.attachTransform"/>
    protected Transform originalAttachTransform
    {
        get => m_OriginalAttachTransform;
        set => m_OriginalAttachTransform = value;
    }

    /// <summary>
    /// The starting transform of any Raycasts. Uses the Original Attach transform, falling back to this transform.
    /// </summary>
    Transform startTransform => m_OriginalAttachTransform != null ? m_OriginalAttachTransform : transform;

    /// <summary>
    /// The closest index of the sample endpoint where a 3D or UI hit occurred.
    /// </summary>
    int closestAnyHitIndex => (m_RaycastHitEndpointIndex > 0 && m_UIRaycastHitEndpointIndex > 0) // Are both valid?
        ? Mathf.Min(m_RaycastHitEndpointIndex, m_UIRaycastHitEndpointIndex) // When both are valid, return the closer one
        : (m_RaycastHitEndpointIndex > 0 ? m_RaycastHitEndpointIndex : m_UIRaycastHitEndpointIndex); // Otherwise return the valid one

    XRUIInputModule m_InputModule;
    XRUIInputModule m_RegisteredInputModule;

    // state to manage hover selection
    XRBaseInteractable m_CurrentNearestObject;
    float m_LastTimeHoveredObjectChanged;
    bool m_PassedHoverTimeToSelect;

    readonly RaycastHit[] m_RaycastHits = new RaycastHit[k_MaxRaycastHits];
    int m_RaycastHitsCount;
    readonly RaycastHitComparer m_RaycastHitComparer = new RaycastHitComparer();

    // Whether switch mode action had been active previous frame
    bool m_SwitchModeActive = false;

    /// <summary>
    /// A polygonal chain represented by a list of endpoints which form line segments
    /// to approximate the curve. Each line segment is where the raycast starts and ends.
    /// World space coordinates.
    /// </summary>
    List<SamplePoint> m_SamplePoints;

    /// <summary>
    /// The <see cref="Time.frameCount"/> that the sample points were updated.
    /// Used as an optimization to avoid recomputing the points during <see cref="ProcessInteractor"/>
    /// when it was already computed and used for an input module in <see cref="UpdateUIModel"/>.
    /// </summary>
    int m_SamplePointsFrameUpdated = -1;

    /// <summary>
    /// The index of the sample endpoint if a 3D hit occurred. Otherwise, a value of <c>0</c> if no hit occurred.
    /// </summary>
    int m_RaycastHitEndpointIndex;

    /// <summary>
    /// The index of the sample endpoint if a UI hit occurred. Otherwise, a value of <c>0</c> if no hit occurred.
    /// </summary>
    int m_UIRaycastHitEndpointIndex;

    /// <summary>
    /// Control points to calculate the quadratic Bezier curve used for aiming.
    /// </summary>
    /// <seealso cref="LineType.BezierCurve"/>
    /// <seealso cref="endPointDistance"/>
    /// <seealso cref="endPointHeight"/>
    /// <seealso cref="controlPointDistance"/>
    /// <seealso cref="controlPointHeight"/>
    readonly Vector3[] m_ControlPoints = new Vector3[3];

    /// <summary>
    /// Control points to calculate the equivalent quadratic Bezier curve to the endpoint where a hit occurred.
    /// </summary>
    readonly Vector3[] m_HitChordControlPoints = new Vector3[3];

    /// <summary>
    /// Reusable list to hold the current sample points.
    /// </summary>
    static List<SamplePoint> s_ScratchSamplePoints;

    /// <summary>
    /// Reusable array to hold the current control points for a quadratic Bezier curve.
    /// </summary>
    static readonly Vector3[] s_ScratchControlPoints = new Vector3[3];

    /// <summary>
    /// See <see cref="MonoBehaviour"/>.
    /// </summary>
    protected void OnValidate()
    {
        m_SampleFrequency = SanitizeSampleFrequency(m_SampleFrequency);
        RegisterOrUnregisterXRUIInputModule();
    }

    /// <inheritdoc />
    protected override void Awake()
    {
        base.Awake();

        var capacity = m_LineType == LineType.StraightLine ? 2 : m_SampleFrequency;
        m_SamplePoints = new List<SamplePoint>(capacity);
        if (s_ScratchSamplePoints == null)
            s_ScratchSamplePoints = new List<SamplePoint>(capacity);

        FindReferenceFrame();

        if (m_OriginalAttachTransform == null)
        {
            m_OriginalAttachTransform = new GameObject($"[{gameObject.name}] Original Attach").transform;
            m_OriginalAttachTransform.SetParent(transform);
            CaptureAttachTransform();
        }

        SetAnchorControlMode(m_AnchorControlMode);
    }

    /// <inheritdoc />
    protected override void OnEnable()
    {
        base.OnEnable();

        if (m_EnableUIInteraction)
            RegisterWithXRUIInputModule();
    }

    /// <inheritdoc />
    protected override void OnDisable()
    {
        base.OnDisable();

        // Clear lines
        m_SamplePoints.Clear();

        if (m_EnableUIInteraction)
            UnregisterFromXRUIInputModule();
    }

    /// <summary>
    /// See <see cref="MonoBehaviour"/>.
    /// </summary>
    protected virtual void OnDrawGizmosSelected()
    {
        if (!Application.isPlaying || m_SamplePoints == null || m_SamplePoints.Count < 2)
        {
            return;
        }

        if (TryGetCurrent3DRaycastHit(out var raycastHit))
        {
            // Draw the normal of the surface at the hit point
            Gizmos.color = new Color(58 / 255f, 122 / 255f, 248 / 255f, 237 / 255f);
            const float length = 0.075f;
            Gizmos.DrawLine(raycastHit.point, raycastHit.point + raycastHit.normal.normalized * length);
        }

        if (TryGetCurrentUIRaycastResult(out var uiRaycastResult))
        {
            // Draw the normal of the surface at the hit point
            Gizmos.color = new Color(58 / 255f, 122 / 255f, 248 / 255f, 237 / 255f);
            const float length = 0.075f;
            Gizmos.DrawLine(uiRaycastResult.worldPosition, uiRaycastResult.worldPosition + uiRaycastResult.worldNormal.normalized * length);
        }

        var hitIndex = closestAnyHitIndex;

        // Draw sample points where the raycast line segments took place
        for (var i = 0; i < m_SamplePoints.Count; ++i)
        {
            var samplePoint = m_SamplePoints[i];

            // Change the color of the points after the segment where a hit happened
            const float radius = 0.025f;
            var color = hitIndex == 0 || i < hitIndex
                ? new Color(163 / 255f, 73 / 255f, 164 / 255f, 0.75f)
                : new Color(205 / 255f, 143 / 255f, 205 / 255f, 0.5f);
            Gizmos.color = color;
            Gizmos.DrawSphere(samplePoint.position, radius);
            if (i < m_SamplePoints.Count - 1)
            {
                var nextPoint = m_SamplePoints[i + 1];
                Gizmos.DrawLine(samplePoint.position, nextPoint.position);
            }
        }

        switch (m_LineType)
        {
            case LineType.ProjectileCurve:
                DrawQuadraticBezierGizmo(m_HitChordControlPoints[0], m_HitChordControlPoints[1], m_HitChordControlPoints[2]);
                break;
            case LineType.BezierCurve:
                DrawQuadraticBezierGizmo(m_ControlPoints[0], m_ControlPoints[1], m_ControlPoints[2]);
                break;
        }
    }

    static void DrawQuadraticBezierGizmo(Vector3 p0, Vector3 p1, Vector3 p2)
    {
        // Draw the control points of the quadratic Bezier curve
        // (P₀ = start point, P₁ = control point, P₂ = end point)
        const float radius = 0.025f;
        Gizmos.color = new Color(1f, 0f, 0f, 0.75f);
        Gizmos.DrawSphere(p0, radius);
        Gizmos.DrawSphere(p1, radius);
        Gizmos.DrawSphere(p2, radius);

        // Draw lines between the control points
        Gizmos.color = new Color(0.5f, 0.5f, 0.5f, 0.75f);
        Gizmos.DrawLine(p0, p1);
        Gizmos.DrawLine(p1, p2);

        // Draw tangent lines along the curve like string art
        // (Q₀ = intermediate start point, Q₁ = intermediate end point, and the linear interpolation between them is the curve)
        Gizmos.color = new Color(0f, 0f, 205 / 25f, 0.75f);
        for (var t = 0.1f; t <= 0.9f; t += 0.1f)
        {
            var q0 = Vector3.Lerp(p0, p1, t);
            var q1 = Vector3.Lerp(p1, p2, t);
            Gizmos.DrawLine(q0, q1);
        }
    }

    /// <summary>
    /// Attempts to locate a reference frame for the curve (if necessary).
    /// </summary>
    /// <seealso cref="referenceFrame"/>
    void FindReferenceFrame()
    {
        if (m_ReferenceFrame != null)
            return;

        var xrRig = FindObjectOfType<XRRig>();
        if (xrRig != null)
        {
            var rig = xrRig.rig;
            if (rig != null)
            {
                m_ReferenceFrame = rig.transform;
            }
            else
            {
                Debug.Log($"Reference frame of the curve not set and {nameof(XRRig)}.{nameof(XRRig.rig)} is not set, using global up as default.", this);
            }
        }
        else
        {
            Debug.Log($"Reference frame of the curve not set and {nameof(XRRig)} is not found, using global up as default.", this);
        }
    }

    void FindOrCreateXRUIInputModule()
    {
        var eventSystem = FindObjectOfType<EventSystem>();
        if (eventSystem == null)
            eventSystem = new GameObject("EventSystem", typeof(EventSystem)).GetComponent<EventSystem>();
        else
        {
            // Remove the Standalone Input Module if already implemented, since it will block the XRUIInputModule
            var standaloneInputModule = eventSystem.GetComponent<StandaloneInputModule>();
            if (standaloneInputModule != null)
                Destroy(standaloneInputModule);
        }

        m_InputModule = eventSystem.GetComponent<XRUIInputModule>();
        if (m_InputModule == null)
            m_InputModule = eventSystem.gameObject.AddComponent<XRUIInputModule>();
    }

    /// <summary>
    /// Register with the <see cref="XRUIInputModule"/> (if necessary).
    /// </summary>
    /// <seealso cref="UnregisterFromXRUIInputModule"/>
    void RegisterWithXRUIInputModule()
    {
        if (m_InputModule == null)
            FindOrCreateXRUIInputModule();

        if (m_RegisteredInputModule == m_InputModule)
            return;

        UnregisterFromXRUIInputModule();

        m_InputModule.RegisterInteractor(this);
        m_RegisteredInputModule = m_InputModule;
    }

    /// <summary>
    /// Unregister from the <see cref="XRUIInputModule"/> (if necessary).
    /// </summary>
    /// <seealso cref="RegisterWithXRUIInputModule"/>
    void UnregisterFromXRUIInputModule()
    {
        if (m_RegisteredInputModule != null)
            m_RegisteredInputModule.UnregisterInteractor(this);

        m_RegisteredInputModule = null;
    }

    /// <summary>
    /// Register with or unregister from the Input Module (if necessary).
    /// </summary>
    /// <remarks>
    /// If this behavior is not active and enabled, this function does nothing.
    /// </remarks>
    void RegisterOrUnregisterXRUIInputModule()
    {
        if (!isActiveAndEnabled || !Application.isPlaying)
            return;

        if (m_EnableUIInteraction)
            RegisterWithXRUIInputModule();
        else
            UnregisterFromXRUIInputModule();
    }

    /// <summary>
    /// (Obsolete) Use <see cref="ILineRenderable.GetLinePoints"/> instead.
    /// </summary>
    /// <param name="linePoints">Obsolete.</param>
    /// <param name="numPoints">Obsolete.</param>
    /// <param name="_">Dummy value to support old function signature.</param>
    /// <returns>Obsolete.</returns>
    [Obsolete("GetLinePoints with ref int parameter has been deprecated. Use signature with out int parameter instead.", true)]
    // ReSharper disable RedundantAssignment
    public bool GetLinePoints(ref Vector3[] linePoints, ref int numPoints, int _ = default)
    // ReSharper restore RedundantAssignment
    {
        return GetLinePoints(ref linePoints, out numPoints);
    }

    /// <inheritdoc />
    public bool GetLinePoints(ref Vector3[] linePoints, out int numPoints)
    {
        if (m_SamplePoints == null || m_SamplePoints.Count < 2)
        {
            numPoints = default;
            return false;
        }

        if (!m_BlendVisualLinePoints)
        {
            numPoints = m_SamplePoints.Count;
            EnsureCapacity(ref linePoints, numPoints);

            for (var i = 0; i < numPoints; ++i)
                linePoints[i] = m_SamplePoints[i].position;

            return true;
        }

        // Because this method may be invoked during OnBeforeRender, the current positions
        // of sample points may be different as the controller moves. Recompute the current
        // positions of sample points.
        UpdateSamplePoints(m_SamplePoints.Count, s_ScratchSamplePoints);

        if (m_LineType == LineType.StraightLine)
        {
            numPoints = 2;
            EnsureCapacity(ref linePoints, numPoints);

            linePoints[0] = s_ScratchSamplePoints[0].position;
            linePoints[1] = m_SamplePoints[m_SamplePoints.Count - 1].position;

            return true;
        }

        // Recompute the equivalent Bezier curve.
        var hitIndex = closestAnyHitIndex;
        CreateBezierCurve(s_ScratchSamplePoints, hitIndex, s_ScratchControlPoints);

        // Blend between the current curve and the sample curve,
        // using the beginning of the current curve and the end of the sample curve.
        // Together it forms a new cubic Bezier curve with control points P₀, P₁, P₂, P₃.
        ElevateQuadraticToCubicBezier(s_ScratchControlPoints[0], s_ScratchControlPoints[1], s_ScratchControlPoints[2],
            out var p0, out var p1, out _, out _);
        ElevateQuadraticToCubicBezier(m_HitChordControlPoints[0], m_HitChordControlPoints[1], m_HitChordControlPoints[2],
            out _, out _, out var p2, out var p3);

        if (hitIndex > 0 && hitIndex != m_SamplePoints.Count - 1 && m_LineType == LineType.ProjectileCurve)
        {
            numPoints = m_SamplePoints.Count;
            EnsureCapacity(ref linePoints, numPoints);

            linePoints[0] = p0;

            // Sample from the blended cubic Bezier curve
            // until the line segment endpoint where the hit occurred.
            var interval = 1f / hitIndex;
            for (var i = 1; i <= hitIndex; ++i)
            {
                // Parametric parameter t where 0 ≤ t ≤ 1
                var percent = i * interval;
                linePoints[i] = SampleCubicBezierPoint(p0, p1, p2, p3, percent);
            }

            // Use the original sample curve beyond that point.
            for (var i = hitIndex + 1; i < m_SamplePoints.Count; ++i)
            {
                linePoints[i] = m_SamplePoints[i].position;
            }
        }
        else
        {
            numPoints = m_SampleFrequency;
            EnsureCapacity(ref linePoints, numPoints);

            linePoints[0] = p0;

            // Sample from the blended cubic Bezier curve
            var interval = 1f / (m_SampleFrequency - 1);
            for (var i = 1; i < m_SampleFrequency; ++i)
            {
                // Parametric parameter t where 0 ≤ t ≤ 1
                var percent = i * interval;
                linePoints[i] = SampleCubicBezierPoint(p0, p1, p2, p3, percent);
            }
        }

        return true;
    }

    static void EnsureCapacity(ref Vector3[] linePoints, int numPoints)
    {
        if (linePoints == null || linePoints.Length < numPoints)
            linePoints = new Vector3[numPoints];
    }

    /// <summary>
    /// (Obsolete) Use <see cref="ILineRenderable.TryGetHitInfo"/> instead.
    /// </summary>
    /// <param name="position">Obsolete.</param>
    /// <param name="normal">Obsolete.</param>
    /// <param name="positionInLine">Obsolete.</param>
    /// <param name="isValidTarget">Obsolete.</param>
    /// <param name="_">Dummy value to support old function signature.</param>
    /// <returns>Obsolete.</returns>
    [Obsolete("TryGetHitInfo with ref parameters has been deprecated. Use signature with out parameters instead.", true)]
    // ReSharper disable RedundantAssignment
    public bool TryGetHitInfo(ref Vector3 position, ref Vector3 normal, ref int positionInLine, ref bool isValidTarget, int _ = default)
    // ReSharper restore RedundantAssignment
    {
        return TryGetHitInfo(out position, out normal, out positionInLine, out isValidTarget);
    }

    /// <inheritdoc />
    public bool TryGetHitInfo(out Vector3 position, out Vector3 normal, out int positionInLine, out bool isValidTarget)
    {
        position = default;
        normal = default;
        positionInLine = default;
        isValidTarget = default;

        if (!TryGetCurrentRaycast(
            out var raycastHit,
            out var raycastHitIndex,
            out var raycastResult,
            out var raycastResultIndex,
            out var isUIHitClosest))
        {
            return false;
        }

        if (raycastResult.HasValue && isUIHitClosest)
        {
            position = raycastResult.Value.worldPosition;
            normal = raycastResult.Value.worldNormal;
            positionInLine = raycastResultIndex;

            isValidTarget = raycastResult.Value.gameObject != null;
        }
        else if (raycastHit.HasValue)
        {
            position = raycastHit.Value.point;
            normal = raycastHit.Value.normal;
            positionInLine = raycastHitIndex;

            // Determine if the collider is registered as an interactable and the interactable is being hovered
            var interactable = interactionManager.GetInteractableForCollider(raycastHit.Value.collider);
            isValidTarget = interactable != null && hoverTargets.Contains(interactable);
        }

        return true;
    }

    /// <inheritdoc />
    public virtual void UpdateUIModel(ref TrackedDeviceModel model)
    {
        if (!isActiveAndEnabled || m_SamplePoints == null)
            return;

        model.position = startTransform.position;
        model.orientation = startTransform.rotation;
        model.select = isUISelectActive;
        model.raycastLayerMask = raycastMask;

        var raycastPoints = model.raycastPoints;
        raycastPoints.Clear();

        // Update curve approximation used for raycasts.
        // This method will be called before ProcessInteractor.
        UpdateSamplePoints(m_SampleFrequency, m_SamplePoints);
        m_SamplePointsFrameUpdated = Time.frameCount;

        var numPoints = m_SamplePoints.Count;
        if (numPoints > 0)
        {
            if (raycastPoints.Capacity < numPoints)
                raycastPoints.Capacity = numPoints;

            for (var i = 0; i < numPoints; ++i)
                raycastPoints.Add(m_SamplePoints[i].position);
        }
    }

    /// <inheritdoc />
    public bool TryGetUIModel(out TrackedDeviceModel model)
    {
        if (m_InputModule != null)
        {
            return m_InputModule.GetTrackedDeviceModel(this, out model);
        }

        model = new TrackedDeviceModel(-1);
        return false;
    }

    /// <inheritdoc cref="TryGetCurrent3DRaycastHit(out RaycastHit)"/>
    [Obsolete("GetCurrentRaycastHit has been deprecated. Use TryGetCurrent3DRaycastHit instead. (UnityUpgradable) -> TryGetCurrent3DRaycastHit(*)")]
    public bool GetCurrentRaycastHit(out RaycastHit raycastHit)
    {
        return TryGetCurrent3DRaycastHit(out raycastHit);
    }

    /// <inheritdoc cref="TryGetCurrent3DRaycastHit(out RaycastHit, out int)"/>
    public bool TryGetCurrent3DRaycastHit(out RaycastHit raycastHit)
    {
        return TryGetCurrent3DRaycastHit(out raycastHit, out _);
    }

    /// <summary>
    /// Gets the first 3D raycast hit, if any raycast hits are available.
    /// </summary>
    /// <param name="raycastHit">When this method returns, contains the raycast hit if available; otherwise, the default value.</param>
    /// <param name="raycastEndpointIndex">When this method returns, contains the index of the sample endpoint if a hit occurred.
    /// Otherwise, a value of <c>0</c> if no hit occurred.</param>
    /// <returns>Returns <see langword="true"/> if a hit occurred, implying the raycast hit information is valid.
    /// Otherwise, returns <see langword="false"/>.</returns>
    public bool TryGetCurrent3DRaycastHit(out RaycastHit raycastHit, out int raycastEndpointIndex)
    {
        if (m_RaycastHitsCount > 0)
        {
            Assert.IsTrue(m_RaycastHits.Length >= m_RaycastHitsCount);
            raycastHit = m_RaycastHits[0];
            raycastEndpointIndex = m_RaycastHitEndpointIndex;
            return true;
        }

        raycastHit = default;
        raycastEndpointIndex = default;
        return false;
    }

    /// <inheritdoc cref="TryGetCurrentUIRaycastResult(out RaycastResult, out int)"/>
    public bool TryGetCurrentUIRaycastResult(out RaycastResult raycastResult)
    {
        return TryGetCurrentUIRaycastResult(out raycastResult, out _);
    }

    /// <summary>
    /// Gets the first UI raycast result, if any raycast results are available.
    /// </summary>
    /// <param name="raycastResult">When this method returns, contains the UI raycast result if available; otherwise, the default value.</param>
    /// <param name="raycastEndpointIndex">When this method returns, contains the index of the sample endpoint if a hit occurred.
    /// Otherwise, a value of <c>0</c> if no hit occurred.</param>
    /// <returns>Returns <see langword="true"/> if a hit occurred, implying the raycast hit information is valid.
    /// Otherwise, returns <see langword="false"/>.</returns>
    public bool TryGetCurrentUIRaycastResult(out RaycastResult raycastResult, out int raycastEndpointIndex)
    {
        if (TryGetUIModel(out var model) && model.currentRaycast.isValid)
        {
            raycastResult = model.currentRaycast;
            raycastEndpointIndex = model.currentRaycastEndpointIndex;
            return true;
        }

        raycastResult = default;
        raycastEndpointIndex = default;
        return false;
    }

    /// <summary>
    /// Gets the first 3D and UI raycast hits, if any raycast hits are available.
    /// </summary>
    /// <param name="raycastHit">When this method returns, contains the raycast hit if available; otherwise, the default value.</param>
    /// <param name="raycastHitIndex">When this method returns, contains the index of the sample endpoint if a hit occurred.
    /// Otherwise, a value of <c>0</c> if no hit occurred.</param>
    /// <param name="uiRaycastHit">When this method returns, contains the UI raycast result if available; otherwise, the default value.</param>
    /// <param name="uiRaycastHitIndex">When this method returns, contains the index of the sample endpoint if a hit occurred.
    /// Otherwise, a value of <c>0</c> if no hit occurred.</param>
    /// <param name="isUIHitClosest">When this method returns, contains whether the UI raycast result was the closest hit.</param>
    /// <returns>Returns <see langword="true"/> if either hit occurred, implying the raycast hit information is valid.
    /// Otherwise, returns <see langword="false"/>.</returns>
    public bool TryGetCurrentRaycast(
        out RaycastHit? raycastHit,
        out int raycastHitIndex,
        out RaycastResult? uiRaycastHit,
        out int uiRaycastHitIndex,
        out bool isUIHitClosest)
    {
        raycastHit = default;
        uiRaycastHit = default;
        isUIHitClosest = default;

        var hitOccurred = false;

        var hitIndex = int.MaxValue;
        var distance = float.MaxValue;
        if (TryGetCurrent3DRaycastHit(out var raycastHitValue, out raycastHitIndex))
        {
            raycastHit = raycastHitValue;
            hitIndex = raycastHitIndex;
            distance = raycastHitValue.distance;

            hitOccurred = true;
        }

        if (TryGetCurrentUIRaycastResult(out var raycastResultValue, out uiRaycastHitIndex))
        {
            uiRaycastHit = raycastResultValue;

            // Determine if the UI hit is closer than the 3D hit.
            // The Raycast segments are sourced from a polygonal chain of endpoints.
            // Within each segment, this Interactor could have hit either a 3D object or a UI object.
            // The distance is just from the segment start position, not from the origin of the whole curve.
            isUIHitClosest = uiRaycastHitIndex > 0 &&
                (uiRaycastHitIndex < hitIndex || (uiRaycastHitIndex == hitIndex && raycastResultValue.distance <= distance));

            hitOccurred = true;
        }

        return hitOccurred;
    }

    /// <summary>
    /// Calculate the quadratic Bezier control points used for <see cref="LineType.BezierCurve"/>.
    /// </summary>
    void UpdateBezierControlPoints()
    {
        var forward = startTransform.forward;
        var up = m_ReferenceFrame != null ? m_ReferenceFrame.up : Vector3.up;
        m_ControlPoints[0] = startTransform.position;
        m_ControlPoints[1] = m_ControlPoints[0] + forward * m_ControlPointDistance + up * m_ControlPointHeight;
        m_ControlPoints[2] = m_ControlPoints[0] + forward * m_EndPointDistance + up * m_EndPointHeight;
    }

    static Vector3 SampleQuadraticBezierPoint(Vector3 p0, Vector3 p1, Vector3 p2, float t)
    {
        var u = 1f - t;   // (1 - t)
        var uu = u * u;   // (1 - t)²
        var tt = t * t;   // t²

        // (1 - t)²P₀ + 2(1 - t)tP₁ + t²P₂ where 0 ≤ t ≤ 1
        // u²P₀ + 2utP₁ + t²P₂
        return (uu * p0) +
            (2f * u * t * p1) +
            (tt * p2);
    }

    static Vector3 SampleCubicBezierPoint(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        var u = 1f - t;   // (1 - t)
        var uu = u * u;   // (1 - t)²
        var uuu = uu * u; // (1 - t)³
        var tt = t * t;   // t²
        var ttt = tt * t; // t³

        // (1 - t)³P₀ + 3(1 - t)²tP₁ + 3(1 - t)t²P₂ + t³P₃ where 0 ≤ t ≤ 1
        // u³P₀ + 3u²tP₁ + 3ut²P₂ + t³P₃
        return (uuu * p0) +
            (3f * uu * t * p1) +
            (3f * u * tt * p2) +
            (ttt * p3);
    }

    static void ElevateQuadraticToCubicBezier(Vector3 p0, Vector3 p1, Vector3 p2, out Vector3 c0, out Vector3 c1, out Vector3 c2, out Vector3 c3)
    {
        // A Bezier curve of one degree can be reproduced by one of higher degree.
        // Convert quadratic Bezier curve with control points P₀, P₁, P₂
        // into a cubic Bezier curve with control points C₀, C₁, C₂, C₃.
        // The end points remain the same.
        c0 = p0;
        c1 = p0 + (2f / 3f) * (p1 - p0);
        c2 = p2 + (2f / 3f) * (p1 - p2);
        c3 = p2;
    }

    static Vector3 SampleProjectilePoint(Vector3 initialPosition, Vector3 initialVelocity, Vector3 constantAcceleration, float time)
    {
        // Position of object in constant acceleration is:
        // x(t) = x₀ + v₀t + 0.5at²
        // where x₀ is the position at time 0,
        // v₀ is the velocity vector at time 0,
        // a is the constant acceleration vector
        return initialPosition + initialVelocity * time + constantAcceleration * (0.5f * time * time);
    }

    void CalculateProjectileParameters(out Vector3 initialPosition, out Vector3 initialVelocity, out Vector3 constantAcceleration, out float flightTime)
    {
        initialPosition = startTransform.position;
        initialVelocity = startTransform.forward * m_Velocity;
        var up = m_ReferenceFrame != null ? m_ReferenceFrame.up : Vector3.up;
        var referencePosition = m_ReferenceFrame != null ? m_ReferenceFrame.position : Vector3.zero;
        constantAcceleration = up * -m_Acceleration;

        // Vertical velocity component Vy = v₀sinθ
        // When initial height = 0,
        // Time of flight = 2(initial velocity)(sine of launch angle) / (acceleration) = 2v₀sinθ/g
        // When initial height > 0,
        // Time of flight = [Vy + √(Vy² + 2gh)] / g
        // The additional flight time property is added.
        var vy = m_Velocity * Mathf.Sin(angle * Mathf.Deg2Rad);
        var height = Vector3.Project(referencePosition - initialPosition, up).magnitude + m_AdditionalGroundHeight;
        if (height < 0f)
            flightTime = m_AdditionalFlightTime;
        else if (Mathf.Approximately(height, 0f))
            flightTime = 2f * vy / m_Acceleration + m_AdditionalFlightTime;
        else
            flightTime = (vy + Mathf.Sqrt(vy * vy + 2f * m_Acceleration * height)) / m_Acceleration + m_AdditionalFlightTime;

        flightTime = Mathf.Max(flightTime, 0f);
    }

    static bool TryRead2DAxis(InputAction action, out Vector2 output)
    {
        if (action != null)
        {
            output = action.ReadValue<Vector2>();
            return true;
        }
        output = default;
        return false;
    }

    /// <summary>
    /// Rotates the attach anchor for this interactor around the X axis. This can be useful to rotate a held object.
    /// </summary>
    /// <param name="anchor">The attach transform of the interactor.</param>
    /// <param name="directionAmount">The rotation amount.</param>
    protected virtual void RotateAnchorX(Transform anchor, float directionAmount)
    {
        if (Mathf.Approximately(directionAmount, 0f))
            return;

        var rotateAngle = directionAmount * (m_RotateSpeed * Time.deltaTime);

        if (m_AnchorRotateReferenceFrame != null)
            anchor.Rotate(m_AnchorRotateReferenceFrame.up, rotateAngle, Space.World);
        else
            anchor.Rotate(Vector3.up, rotateAngle);
    }

    /// <summary>
    /// Rotates the attach anchor for this interactor around the Y axis. This can be useful to rotate a held object.
    /// </summary>
    /// <param name="anchor">The attach transform of the interactor.</param>
    /// <param name="directionAmount">The rotation amount.</param>
    protected virtual void RotateAnchorY(Transform anchor, float directionAmount)
    {
        if (Mathf.Approximately(directionAmount, 0f))
            return;

        var rotateAngle = directionAmount * (m_RotateSpeed * Time.deltaTime);

        if (m_AnchorRotateReferenceFrame != null)
            anchor.Rotate(m_AnchorRotateReferenceFrame.forward, rotateAngle, Space.World);
        else
            anchor.Rotate(Vector3.forward, rotateAngle);
    }

    /// <summary>
    /// Rotates the attach anchor for this interactor around the Z axis. This can be useful to rotate a held object.
    /// </summary>
    /// <param name="anchor">The attach transform of the interactor.</param>
    /// <param name="directionAmount">The rotation amount.</param>
    protected virtual void RotateAnchorZ(Transform anchor, float directionAmount)
    {
        if (Mathf.Approximately(directionAmount, 0f))
            return;

        var rotateAngle = directionAmount * (m_RotateSpeed * Time.deltaTime);

        if (m_AnchorRotateReferenceFrame != null)
            anchor.Rotate(m_AnchorRotateReferenceFrame.right, rotateAngle, Space.World);
        else
            anchor.Rotate(Vector3.right, rotateAngle);
    }

    /// <summary>
    /// Translates the attach anchor for this interactor. This can be useful to move a held object closer or further away from the interactor.
    /// </summary>
    /// <param name="originalAnchor">The original attach transform of the interactor.</param>
    /// <param name="anchor">The attach transform of the interactor.</param>
    /// <param name="directionAmount">The translation amount.</param>
    protected virtual void TranslateAnchor(Transform originalAnchor, Transform anchor, float directionAmount)
    {
        if (Mathf.Approximately(directionAmount, 0f))
            return;

        var originalAnchorPosition = originalAnchor.position;
        var originalAnchorForward = originalAnchor.forward;

        var resultingPosition = attachTransform.position + originalAnchorForward * (directionAmount * m_TranslateSpeed * Time.deltaTime);

        // Check the delta between the original position, and the calculated position. stop the new position
        var posInAttachSpace = resultingPosition - originalAnchorPosition;
        var dotResult = Vector3.Dot(posInAttachSpace, originalAnchorForward);

        attachTransform.position = dotResult > 0f ? resultingPosition : originalAnchorPosition;
    }

    /// <summary>
    /// Scales the attach anchor for this interactor. This can be useful to change the size of a held object.
    /// </summary>
    /// <param name="originalAnchor">The original attach transform of the interactor.</param>
    /// <param name="anchor">The attach transform of the interactor.</param>
    /// <param name="directionAmount">The translation amount.</param>
    protected virtual void ScaleAnchor(Transform anchor, float directionAmount)
    {
        if (Mathf.Approximately(directionAmount, 0f))
            return;

        var scaleDelta = directionAmount * (m_ScaleSpeed * Time.deltaTime);

        anchor.localScale += scaleDelta * anchor.localScale;
    }

    /// <inheritdoc />
    public override void ProcessInteractor(XRInteractionUpdateOrder.UpdatePhase updatePhase)
    {
        base.ProcessInteractor(updatePhase);

        if (updatePhase == XRInteractionUpdateOrder.UpdatePhase.Dynamic)
        {
            // Update the pose of the attach point
            if (m_AllowAnchorControl && selectTarget != null)
            {
                var ctrl = xrController as XRController;
                if (ctrl != null && ctrl.inputDevice.isValid)
                {
                    ctrl.inputDevice.IsPressed(ctrl.rotateObjectLeft, out var leftPressed, ctrl.axisToPressThreshold);
                    ctrl.inputDevice.IsPressed(ctrl.rotateObjectRight, out var rightPressed, ctrl.axisToPressThreshold);

                    ctrl.inputDevice.IsPressed(ctrl.moveObjectIn, out var inPressed, ctrl.axisToPressThreshold);
                    ctrl.inputDevice.IsPressed(ctrl.moveObjectOut, out var outPressed, ctrl.axisToPressThreshold);

                    if (inPressed || outPressed)
                    {
                        var directionAmount = inPressed ? 1f : -1f;
                        TranslateAnchor(m_OriginalAttachTransform, attachTransform, directionAmount);
                    }
                    if (leftPressed || rightPressed)
                    {
                        var directionAmount = leftPressed ? -1f : 1f;
                        RotateAnchorX(attachTransform, directionAmount);
                    }
                }

                var actionBasedController = xrController as ActionBasedController;
                if (actionBasedController != null)
                {
                    if (TryRead2DAxis(actionBasedController.rotateAnchorAction.action, out var rotateAmt))
                    {
                        RotateAnchorX(attachTransform, rotateAmt.x);
                    }

                    if (TryRead2DAxis(actionBasedController.translateAnchorAction.action, out var translateAmt))
                    {
                        TranslateAnchor(m_OriginalAttachTransform, attachTransform, translateAmt.y);
                    }
                }

                var editController = xrController as EditController;
                if (editController != null)
                {
                    var switchModeAction = editController.switchInteractionModeAction.action;
                    var triggerSwitchMode = switchModeAction != null && switchModeAction.triggered;

                    TryRead2DAxis(editController.switchInteractionModeAction.action, out var switchAmt);
                    var released = Mathf.Approximately(switchAmt.x, 0);

                    if (triggerSwitchMode && !released && !m_SwitchModeActive)
                    {
                        // Switch Mode
                        if (switchAmt.x > 0.7)
                        {
                            NextAnchorControlMode();
                            m_SwitchModeActive = true;
                        }
                        else if (switchAmt.x < -0.7)
                        {
                            PreviousAnchorControlMode();
                            m_SwitchModeActive = true;
                        }
                    }
                    if (released && !triggerSwitchMode)
                        m_SwitchModeActive = false;

                    if (TryRead2DAxis(editController.interactionAxisAction.action, out var interactAmt))
                    {
                        if (Math.Abs(interactAmt.y) > 0.7)
                        {
                            switch (m_AnchorControlMode)
                            {
                                case AnchorControlMode.Translate:
                                    TranslateAnchor(m_OriginalAttachTransform, attachTransform, interactAmt.y);
                                    break;
                                case AnchorControlMode.Scale:
                                    ScaleAnchor(attachTransform, interactAmt.y);
                                    break;
                                case AnchorControlMode.RotateX:
                                    RotateAnchorX(attachTransform, interactAmt.y);
                                    break;
                                case AnchorControlMode.RotateY:
                                    RotateAnchorY(attachTransform, interactAmt.y);
                                    break;
                                case AnchorControlMode.RotateZ:
                                    RotateAnchorZ(attachTransform, interactAmt.y);
                                    break;

                            }
                        }
                    }
                }
            }

            // Update curve approximation used for raycasts
            // if it hasn't already been done earlier in the frame for the UI Input Module.
            if (m_SamplePointsFrameUpdated != Time.frameCount)
            {
                UpdateSamplePoints(m_SampleFrequency, m_SamplePoints);
                m_SamplePointsFrameUpdated = Time.frameCount;
            }

            Assert.IsTrue(m_SamplePoints.Count >= 2);

            // Perform raycasts and store the equivalent Bezier curve to the endpoint where a hit occurred (used for blending)
            UpdateRaycastHits();
            UpdateUIHitIndex();
            CreateBezierCurve(m_SamplePoints, closestAnyHitIndex, m_HitChordControlPoints);

            // Determine the Interactables that this Interactor could possibly interact with this frame
            GetValidTargets(m_ValidTargets);

            // Check to see if we have a new hover object
            var nearestObject = m_ValidTargets.FirstOrDefault();
            if (nearestObject != m_CurrentNearestObject)
            {
                m_CurrentNearestObject = nearestObject;
                m_LastTimeHoveredObjectChanged = Time.time;
                m_PassedHoverTimeToSelect = false;
            }
            else if (!m_PassedHoverTimeToSelect && nearestObject != null)
            {
                var progressToHoverSelect = Mathf.Clamp01((Time.time - m_LastTimeHoveredObjectChanged) / m_HoverTimeToSelect);
                if (progressToHoverSelect >= 1f)
                    m_PassedHoverTimeToSelect = true;
            }
        }
    }

    /// <inheritdoc />
    public override bool isSelectActive
    {
        get
        {
            if (m_HoverToSelect && m_PassedHoverTimeToSelect)
                return true;

            return base.isSelectActive;
        }
    }

    /// <inheritdoc />
    public override void GetValidTargets(List<XRBaseInteractable> targets)
    {
        targets.Clear();

        if (m_RaycastHitsCount > 0)
        {
            var hasUIHit = TryGetCurrentUIRaycastResult(out var uiRaycastResult, out var uiHitIndex);
            for (var i = 0; i < m_RaycastHitsCount; ++i)
            {
                var raycastHit = m_RaycastHits[i];

                // A hit on UI should block Interactables behind it from being a valid target
                if (hasUIHit && uiHitIndex > 0 && (uiHitIndex < m_RaycastHitEndpointIndex || (uiHitIndex == m_RaycastHitEndpointIndex && uiRaycastResult.distance <= raycastHit.distance)))
                    break;

                // A hit on geometry not associated with Interactables should block Interactables behind it from being a valid target
                var interactable = interactionManager.GetInteractableForCollider(raycastHit.collider);
                if (interactable == null)
                    break;

                if (!targets.Contains(interactable))
                {
                    targets.Add(interactable);

                    // Stop after the first if enabled
                    if (m_HitClosestOnly)
                        break;
                }
            }
        }
    }

    /// <summary>
    /// Approximate the curve into a polygonal chain of endpoints, whose line segments can be used as
    /// the rays for doing Physics raycasts.
    /// </summary>
    /// <param name="count">The number of sample points to calculate.</param>
    /// <param name="samplePoints">The result list of sample points to populate.</param>
    void UpdateSamplePoints(int count, List<SamplePoint> samplePoints)
    {
        Assert.IsTrue(count >= 2);

        samplePoints.Clear();
        var samplePoint = new SamplePoint
        {
            position = startTransform.position,
            parameter = 0f,
        };
        samplePoints.Add(samplePoint);

        switch (m_LineType)
        {
            case LineType.StraightLine:
                samplePoint.position = samplePoints[0].position + startTransform.forward * m_MaxRaycastDistance;
                samplePoint.parameter = 1f;
                samplePoints.Add(samplePoint);
                break;
            case LineType.ProjectileCurve:
                {
                    CalculateProjectileParameters(out var initialPosition, out var initialVelocity, out var constantAcceleration, out var flightTime);

                    var interval = flightTime / (count - 1);
                    for (var i = 1; i < count; ++i)
                    {
                        var time = i * interval;
                        samplePoint.position = SampleProjectilePoint(initialPosition, initialVelocity, constantAcceleration, time);
                        samplePoint.parameter = time;
                        samplePoints.Add(samplePoint);
                    }
                }
                break;
            case LineType.BezierCurve:
                {
                    // Update control points for Bezier curve
                    UpdateBezierControlPoints();
                    var p0 = m_ControlPoints[0];
                    var p1 = m_ControlPoints[1];
                    var p2 = m_ControlPoints[2];

                    var interval = 1f / (count - 1);
                    for (var i = 1; i < count; ++i)
                    {
                        // Parametric parameter t where 0 ≤ t ≤ 1
                        var percent = i * interval;
                        samplePoint.position = SampleQuadraticBezierPoint(p0, p1, p2, percent);
                        samplePoint.parameter = percent;
                        samplePoints.Add(samplePoint);
                    }
                }
                break;
        }
    }

    /// <summary>
    /// Walk the line segments from the approximated curve, casting from one endpoint to the next.
    /// </summary>
    void UpdateRaycastHits()
    {
        m_RaycastHitsCount = 0;
        m_RaycastHitEndpointIndex = 0;

        for (var i = 1; i < m_SamplePoints.Count; ++i)
        {
            var fromPoint = m_SamplePoints[i - 1].position;
            var toPoint = m_SamplePoints[i].position;

            m_RaycastHitsCount = CheckCollidersBetweenPoints(fromPoint, toPoint);
            if (m_RaycastHitsCount > 0)
            {
                m_RaycastHitEndpointIndex = i;
                // Sort all the hits by distance along the curve
                // since the results of the raycast are not ordered.
                InteractorUtils.Sort(m_RaycastHits, m_RaycastHitComparer);
                break;
            }
        }
    }

    int CheckCollidersBetweenPoints(Vector3 from, Vector3 to)
    {
        Array.Clear(m_RaycastHits, 0, k_MaxRaycastHits);

        // Cast from last point to next point to check if there are hits in between
        if (m_HitDetectionType == HitDetectionType.SphereCast && m_SphereCastRadius > 0f)
        {
            return Physics.SphereCastNonAlloc(from, m_SphereCastRadius, (to - from).normalized,
                m_RaycastHits, Vector3.Distance(to, from), raycastMask, raycastTriggerInteraction);
        }

        return Physics.RaycastNonAlloc(from, (to - from).normalized,
            m_RaycastHits, Vector3.Distance(to, from), raycastMask, raycastTriggerInteraction);
    }

    void UpdateUIHitIndex()
    {
        TryGetCurrentUIRaycastResult(out _, out m_UIRaycastHitEndpointIndex);
    }

    void CreateBezierCurve(List<SamplePoint> samplePoints, int endSamplePointIndex, Vector3[] quadraticControlPoints)
    {
        // Convert the raycast curve ranging from the controller to the sample endpoint
        // where the hit occurred into a quadratic Bezier curve
        // with control points P₀, P₁, P₂.
        var endSamplePoint = endSamplePointIndex > 0 && endSamplePointIndex < samplePoints.Count
            ? samplePoints[endSamplePointIndex]
            : samplePoints[samplePoints.Count - 1];
        var p2 = endSamplePoint.position;
        var p0 = samplePoints[0].position;

        var midpoint = 0.5f * (p0 + p2);

        switch (m_LineType)
        {
            case LineType.StraightLine:
                quadraticControlPoints[0] = p0;
                quadraticControlPoints[1] = midpoint;
                quadraticControlPoints[2] = p2;
                break;
            case LineType.ProjectileCurve:
                CalculateProjectileParameters(out _, out var initialVelocity, out var constantAcceleration, out _);

                var midTime = 0.5f * endSamplePoint.parameter;
                var sampleMidTime = SampleProjectilePoint(p0, initialVelocity, constantAcceleration, midTime);
                var p1 = midpoint + 2f * (sampleMidTime - midpoint);

                quadraticControlPoints[0] = p0;
                quadraticControlPoints[1] = p1;
                quadraticControlPoints[2] = p2;
                break;
            case LineType.BezierCurve:
                Assert.IsTrue(m_ControlPoints[0] == p0);
                quadraticControlPoints[0] = m_ControlPoints[0];
                quadraticControlPoints[1] = m_ControlPoints[1];
                quadraticControlPoints[2] = m_ControlPoints[2];
                break;
        }
    }

    /// <inheritdoc />
    public override bool CanHover(XRBaseInteractable interactable)
    {
        return base.CanHover(interactable) && (selectTarget == null || selectTarget == interactable);
    }

    /// <inheritdoc />
    public override bool CanSelect(XRBaseInteractable interactable)
    {
        var selectActivated = (m_HoverToSelect && m_PassedHoverTimeToSelect && m_CurrentNearestObject == interactable) || base.CanSelect(interactable);

        // Check if selectTarget is a valid target or if we enable sticky select to fake selectTarget as valid when we selected it but are not pointing at it.
        return selectActivated &&
               (selectTarget == null || (selectTarget == interactable && (keepSelectedTargetValid || m_ValidTargets.Contains(interactable))));
    }

    /// <inheritdoc />
    protected override void OnSelectEntering(SelectEnterEventArgs args)
    {
        base.OnSelectEntering(args);

        CaptureAttachTransform();

        if (!m_UseForceGrab && TryGetCurrent3DRaycastHit(out var raycastHit))
            attachTransform.position = raycastHit.point;

        // Reset some Values
        if (m_ResetAnchorControlMode)
            SetAnchorControlMode(AnchorControlMode.Translate);
        attachTransform.localScale = new Vector3(1, 1, 1);
        // Show current anchor control mode
        ShowAnchorControlMode(true);
    }

    /// <inheritdoc />
    protected override void OnSelectExiting(SelectExitEventArgs args)
    {
        base.OnSelectExiting(args);
        RestoreAttachTransform();
        // Don't show current anchor control mode
        ShowAnchorControlMode(false);
    }

    void CaptureAttachTransform()
    {
        m_OriginalAttachTransform.position = attachTransform.position;
        m_OriginalAttachTransform.rotation = attachTransform.rotation;
    }

    void RestoreAttachTransform()
    {
        attachTransform.position = m_OriginalAttachTransform.position;
        attachTransform.rotation = m_OriginalAttachTransform.rotation;
    }

    void ShowAnchorControlMode(bool value)
    {
        if (m_AllowAnchorControl || value == false)
            m_AnchorControlModeText.enabled = value;
    }

    void SetAnchorControlMode(AnchorControlMode mode)
    {
        m_AnchorControlMode = mode;
        m_AnchorControlModeText.text = mode.ToString();
    }

    void NextAnchorControlMode()
    {
        SetAnchorControlMode(InteractorUtils.Next(m_AnchorControlMode));
    }

    void PreviousAnchorControlMode()
    {
        SetAnchorControlMode(InteractorUtils.Previous(m_AnchorControlMode));
    }

    static int SanitizeSampleFrequency(int value)
    {
        // Upper range does not need to be enforced, just the minimum.
        // The max const just provides a reasonable slider range.
        return Mathf.Max(value, k_MinSampleFrequency);
    }

    /// <summary>
    /// A point within a polygonal chain of endpoints which form line segments
    /// to approximate the curve. Each line segment is where the raycast starts and ends.
    /// </summary>
    struct SamplePoint
    {
        /// <summary>
        /// The world space position of the sample.
        /// </summary>
        public Vector3 position { get; set; }

        /// <summary>
        /// For <see cref="LineType.ProjectileCurve"/>, this represents flight time at the sample.
        /// For <see cref="LineType.BezierCurve"/> and <see cref="LineType.StraightLine"/>, this represents
        /// the parametric parameter <i>t</i> of the curve at the sample (with range [0, 1]).
        /// </summary>
        public float parameter { get; set; }
    }
}
