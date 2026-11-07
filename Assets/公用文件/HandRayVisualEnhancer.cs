using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;

/// <summary>
/// Gives both hand rays a clean, uniformly visible appearance without changing shared XRI samples.
/// </summary>
[DisallowMultipleComponent]
public sealed class HandRayVisualEnhancer : MonoBehaviour
{
    [SerializeField, Range(0.0001f, 0.05f)]
    [Tooltip("Uniform hand-ray width in world-space metres.")]
    private float lineWidth = 0.004f;

    [SerializeField]
    [Tooltip("Color used for valid, invalid, and blocked hand-ray states.")]
    private Color lineColor = Color.white;

    private void Awake()
    {
        Apply();
    }

    private void OnEnable()
    {
        Apply();
    }

    private void OnValidate()
    {
        lineWidth = Mathf.Clamp(lineWidth, 0.0001f, 0.05f);
        Apply();
    }

    private void Apply()
    {
        var lineVisuals = GetComponentsInChildren<XRInteractorLineVisual>(true);
        var solidColorGradient = CreateSolidGradient(lineColor);
        var uniformWidth = AnimationCurve.Linear(0f, 1f, 1f, 1f);

        foreach (var lineVisual in lineVisuals)
        {
            if (!IsUnderHand(lineVisual.transform))
                continue;

            lineVisual.lineWidth = lineWidth;
            lineVisual.widthCurve = uniformWidth;
            lineVisual.validColorGradient = solidColorGradient;
            lineVisual.invalidColorGradient = solidColorGradient;
            lineVisual.blockedColorGradient = solidColorGradient;
            lineVisual.reticle = null;
            lineVisual.blockedReticle = null;
        }
    }

    private static bool IsUnderHand(Transform child)
    {
        for (var current = child; current != null; current = current.parent)
        {
            if (current.name == "Left Hand" || current.name == "Right Hand")
                return true;
        }

        return false;
    }

    private static Gradient CreateSolidGradient(Color color)
    {
        return new Gradient
        {
            colorKeys = new[]
            {
                new GradientColorKey(color, 0f),
                new GradientColorKey(color, 1f),
            },
            alphaKeys = new[]
            {
                new GradientAlphaKey(color.a, 0f),
                new GradientAlphaKey(color.a, 1f),
            },
        };
    }
}
