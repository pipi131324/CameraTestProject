using System;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Swaps the player's model at runtime. Every model lives under the player root, so position,
/// camera and movement carry over; only the Avatar and the per-character sizes change.
/// Each model needs a differently named armature root, because Avatar paths are relative
/// to the Animator and two identical paths would be ambiguous.
/// </summary>
[RequireComponent(typeof(Animator), typeof(CharacterController))]
public class CharacterSwitcher : MonoBehaviour
{
    [Serializable]
    public class Character
    {
        public string displayName;
        public Key key;
        public Avatar avatar;
        [Tooltip("Armature and mesh objects of this model, direct children of the player.")]
        public GameObject[] parts;
        public float controllerHeight = 1.8f;
        public float controllerCenterY = 0.93f;
        public float cameraRootHeight = 1.375f;
    }

    [SerializeField] private Character[] characters;
    [SerializeField] private Transform cameraRoot;
    [SerializeField] private int startIndex;

    private Animator _animator;
    private CharacterController _controller;
    private int _current = -1;

    /// <summary>Raised after a model swap, once the Animator has been rebound.</summary>
    public event Action CharacterChanged;

    public Character Current => _current >= 0 ? characters[_current] : null;

    private void Awake()
    {
        _animator = GetComponent<Animator>();
        _controller = GetComponent<CharacterController>();
        Select(startIndex);
    }

    private void Update()
    {
        var keyboard = Keyboard.current;
        if (keyboard == null) return;

        for (int i = 0; i < characters.Length; i++)
        {
            if (keyboard[characters[i].key].wasPressedThisFrame)
            {
                Select(i);
                return;
            }
        }
    }

    public void Select(int index)
    {
        if (index == _current || index < 0 || index >= characters.Length) return;

        for (int i = 0; i < characters.Length; i++)
        {
            foreach (var part in characters[i].parts)
                part.SetActive(i == index);
        }

        var next = characters[index];
        _animator.avatar = next.avatar;
        _animator.Rebind();

        _controller.height = next.controllerHeight;
        _controller.center = new Vector3(0f, next.controllerCenterY, 0f);

        if (cameraRoot != null)
        {
            var p = cameraRoot.localPosition;
            cameraRoot.localPosition = new Vector3(p.x, next.cameraRootHeight, p.z);
        }

        _current = index;
        CharacterChanged?.Invoke();
    }
}
