using System;
using UnityEngine;
using UnityEngine.EventSystems;
using Object = UnityEngine.Object;

namespace Hkmp.Ui.Component;

/// <inheritdoc />
internal abstract class Component : IComponent {
    /// <summary>
    /// The opacity of non-interactable components.
    /// </summary>
    protected const float NotInteractableOpacity = 0.5f;

    /// <summary>
    /// The underlying GameObject of the component. 
    /// </summary>
    protected readonly GameObject GameObject;

    /// <summary>
    /// The Unity RectTransform instance.
    /// </summary>
    private readonly RectTransform _transform;

    /// <summary>
    /// Whether this component is active.
    /// </summary>
    private bool _activeSelf;

    /// <summary>
    /// The component group this component belongs to.
    /// </summary>
    private readonly ComponentGroup _componentGroup;

    /// <summary>
    /// Whether new components keep a fixed distance from the screen centre. Proportional anchors stretch
    /// distances but not sizes when the window is not 16:9, which pulls apart edges meant to line up.
    /// </summary>
    public static bool CreateCentered { get; set; }

    /// <summary>
    /// Whether this component keeps a fixed distance from the screen centre.
    /// </summary>
    private readonly bool _centered = CreateCentered;

    protected Component(ComponentGroup componentGroup, Vector2 position, Vector2 size) {
        // Create a gameobject with the CanvasRenderer component, so we can render as GUI
        GameObject = new GameObject();
        GameObject.AddComponent<CanvasRenderer>();
        // Make sure game object persists
        Object.DontDestroyOnLoad(GameObject);

        // Create a RectTransform with the desired size
        _transform = GameObject.AddComponent<RectTransform>();

        ApplyPosition(position);

        _transform.sizeDelta = size;

        GameObject.transform.SetParent(UiManager.UiGameObject.transform, false);

        _activeSelf = true;

        _componentGroup = componentGroup;
        componentGroup?.AddComponent(this);
    }

    /// <inheritdoc />
    public virtual void SetGroupActive(bool groupActive) {
        // TODO: figure out why this could be happening
        if (GameObject == null) {
            // Logger.Info(
            //     $"The GameObject belonging to this component (type: {GetType()}) is null, this shouldn't happen");
            return;
        }

        GameObject.SetActive(_activeSelf && groupActive);
    }

    /// <inheritdoc />
    public virtual void SetActive(bool active) {
        _activeSelf = active;

        GameObject.SetActive(_activeSelf && _componentGroup.IsActive());
    }

    /// <inheritdoc />
    public Vector2 GetPosition() {
        if (_centered) {
            return _transform.anchoredPosition + new Vector2(960f, 540f);
        }

        var position = _transform.anchorMin;
        return new Vector2(
            position.x * 1920f,
            position.y * 1080f
        );
    }

    /// <inheritdoc />
    public void SetPosition(Vector2 position) {
        ApplyPosition(position);
    }

    private void ApplyPosition(Vector2 position) {
        if (_centered) {
            _transform.anchorMin = _transform.anchorMax = new Vector2(0.5f, 0.5f);
            _transform.anchoredPosition = position - new Vector2(960f, 540f);
            return;
        }

        _transform.anchorMin = _transform.anchorMax = new Vector2(
            position.x / 1920f,
            position.y / 1080f
        );
    }

    /// <inheritdoc />
    public Vector2 GetSize() {
        return _transform.sizeDelta;
    }

    /// <summary>
    /// Destroys the component.
    /// </summary>
    public void Destroy() {
        Object.Destroy(GameObject);
    }

    /// <summary>
    /// Add an event trigger to this component object.
    /// </summary>
    /// <param name="eventTrigger">The event trigger.</param>
    /// <param name="type">The type of the event trigger.</param>
    /// <param name="action">The action that is executed on the event.</param>
    protected void AddEventTrigger(
        EventTrigger eventTrigger,
        EventTriggerType type,
        Action<BaseEventData> action
    ) {
        var eventTriggerEntry = new EventTrigger.Entry {
            eventID = type
        };
        eventTriggerEntry.callback.AddListener(action.Invoke);

        eventTrigger.triggers.Add(eventTriggerEntry);
    }
}
