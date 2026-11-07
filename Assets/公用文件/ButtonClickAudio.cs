using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Explicitly wired as the first Button.onClick listener on approved business UI.
/// A shared 2D source lets the short click finish when the button closes a panel
/// or loads another scene, without interrupting narration or microphone audio.
/// </summary>
[DisallowMultipleComponent]
[RequireComponent(typeof(Button))]
public sealed class ButtonClickAudio : MonoBehaviour
{
    [SerializeField] private AudioClip clickSound;
    [SerializeField, Range(0f, 1f)] private float volume = 0.65f;

    private static AudioSource sharedSource;
    private static bool quitting;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        sharedSource = null;
        quitting = false;
    }

    public void PlayClickSound()
    {
        if (!Application.isPlaying || quitting || !isActiveAndEnabled || clickSound == null)
            return;

        var button = GetComponent<Button>();
        if (button == null || !button.IsActive() || !button.IsInteractable())
            return;

        if (sharedSource == null)
        {
            var owner = new GameObject("UI Button Click Audio");
            DontDestroyOnLoad(owner);
            sharedSource = owner.AddComponent<AudioSource>();
            sharedSource.playOnAwake = false;
            sharedSource.loop = false;
            sharedSource.spatialBlend = 0f;
            sharedSource.dopplerLevel = 0f;
            sharedSource.priority = 64;
        }

        sharedSource.PlayOneShot(clickSound, Mathf.Clamp01(volume));
    }

    private void OnApplicationQuit()
    {
        quitting = true;
    }
}
