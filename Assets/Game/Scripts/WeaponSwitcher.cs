using System;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// Swaps the weapon in the player's right hand with the top-row number keys.
/// Weapons are parented to the current model's hand bone, and an arms-only Animator layer
/// holds the pose, so the legs keep running while the arms carry the weapon.
/// A weapon prefab may contain a child named "LeftHandGrip" for the left hand to hold via IK.
/// </summary>
[RequireComponent(typeof(Animator))]
public class WeaponSwitcher : MonoBehaviour
{
    [Serializable]
    public class Weapon
    {
        public string displayName;
        public Key key;
        [Tooltip("Leave empty for bare hands.")]
        public GameObject prefab;
        [Tooltip("Value of the WeaponId Animator parameter that selects this weapon's arm pose.")]
        public int animatorId;
        [Tooltip("Weapon position and rotation relative to the right hand bone.")]
        public Vector3 gripPosition;
        public Vector3 gripEuler;

        [NonSerialized] public GameObject instance;
        [NonSerialized] public Transform leftHandGrip;
    }

    private const string LeftHandGripName = "LeftHandGrip";
    private static readonly int WeaponIdHash = Animator.StringToHash("WeaponId");

    [SerializeField] private Weapon[] weapons;
    [SerializeField] private int startIndex;
    [SerializeField] private int weaponLayer = 1;
    [SerializeField] private float leftHandBlendSpeed = 10f;

    private Animator _animator;
    private CharacterSwitcher _characters;
    private int _current = -1;
    private float _leftHandWeight;

    public Weapon Current => _current >= 0 ? weapons[_current] : null;

    private void Awake()
    {
        _animator = GetComponent<Animator>();
        _characters = GetComponent<CharacterSwitcher>();

        foreach (var weapon in weapons)
        {
            if (weapon.prefab == null) continue;
            weapon.instance = Instantiate(weapon.prefab);
            weapon.instance.name = weapon.prefab.name;
            weapon.instance.SetActive(false);
            weapon.leftHandGrip = FindChild(weapon.instance.transform, LeftHandGripName);
        }
    }

    private void OnEnable()
    {
        if (_characters != null) _characters.CharacterChanged += OnCharacterChanged;
    }

    private void OnDisable()
    {
        if (_characters != null) _characters.CharacterChanged -= OnCharacterChanged;
    }

    private void Start()
    {
        AttachToHand();
        Equip(startIndex);
    }

    private void Update()
    {
        var keyboard = Keyboard.current;
        if (keyboard == null) return;

        for (int i = 0; i < weapons.Length; i++)
        {
            if (keyboard[weapons[i].key].wasPressedThisFrame)
            {
                Equip(i);
                return;
            }
        }
    }

    public void Equip(int index)
    {
        if (index == _current || index < 0 || index >= weapons.Length) return;

        for (int i = 0; i < weapons.Length; i++)
        {
            if (weapons[i].instance != null) weapons[i].instance.SetActive(i == index);
        }

        _current = index;
        _animator.SetInteger(WeaponIdHash, weapons[index].animatorId);
    }

    private void OnCharacterChanged()
    {
        // Rebind on the Animator clears its parameters, and the old hand bone is now inactive.
        AttachToHand();
        if (_current >= 0) _animator.SetInteger(WeaponIdHash, weapons[_current].animatorId);
    }

    private void AttachToHand()
    {
        var hand = _animator.GetBoneTransform(HumanBodyBones.RightHand);
        if (hand == null) return;

        foreach (var weapon in weapons)
        {
            if (weapon.instance == null) continue;
            var t = weapon.instance.transform;
            t.SetParent(hand, false);
            t.localPosition = weapon.gripPosition;
            t.localRotation = Quaternion.Euler(weapon.gripEuler);
        }
    }

    private void OnAnimatorIK(int layerIndex)
    {
        if (layerIndex != weaponLayer) return;

        var grip = Current?.leftHandGrip;
        float target = grip != null ? 1f : 0f;
        _leftHandWeight = Mathf.MoveTowards(_leftHandWeight, target, leftHandBlendSpeed * Time.deltaTime);

        _animator.SetIKPositionWeight(AvatarIKGoal.LeftHand, _leftHandWeight);
        if (grip != null) _animator.SetIKPosition(AvatarIKGoal.LeftHand, grip.position);
    }

    private static Transform FindChild(Transform root, string childName)
    {
        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            if (t.name == childName) return t;
        }
        return null;
    }
}
