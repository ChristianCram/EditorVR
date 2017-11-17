#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.Experimental.EditorVR.Core;
using UnityEditor.Experimental.EditorVR.Proxies;
using UnityEditor.Experimental.EditorVR.Utilities;
using UnityEngine;
using UnityEngine.InputNew;

namespace UnityEditor.Experimental.EditorVR.Tools
{
    sealed class SelectionTool : MonoBehaviour, ITool, IUsesRayOrigin, IUsesRaycastResults, ICustomActionMap,
        ISetHighlight, ISelectObject, ISetManipulatorsVisible, IIsHoveringOverUI, IUsesDirectSelection, ILinkedObject,
        ICanGrabObject, IGetManipulatorDragState, IUsesNode, IGetRayVisibility, IIsMainMenuVisible, IIsInMiniWorld,
        IRayToNode, IGetDefaultRayColor, ISetDefaultRayColor, ITooltip, ITooltipPlacement, ISetTooltipVisibility,
        IUsesDeviceType, IMenuIcon, IRequestFeedback
    {
        const float k_MultiselectHueShift = 0.5f;
        static readonly Vector3 k_TooltipPosition = new Vector3(0, -0.15f, -0.13f);
        static readonly Quaternion k_TooltipRotation = Quaternion.AngleAxis(90, Vector3.right);

        // Local method use only -- created here to reduce garbage collection
        static readonly Dictionary<Transform, GameObject> k_TempHovers = new Dictionary<Transform, GameObject>();

        [SerializeField]
        Sprite m_Icon;

        [SerializeField]
        ActionMap m_ActionMap;

        GameObject m_PressedObject;

        SelectionInput m_SelectionInput;

        float m_LastMultiSelectClickTime;
        Color m_NormalRayColor;
        Color m_MultiselectRayColor;
        bool m_MultiSelect;
        bool m_HasDirectHover;

        readonly BindingDictionary m_Controls = new BindingDictionary();
        readonly List<ProxyFeedbackRequest> m_SelectFeedback = new List<ProxyFeedbackRequest>();
        readonly List<ProxyFeedbackRequest> m_DirectSelectFeedback = new List<ProxyFeedbackRequest>();

        readonly Dictionary<Transform, GameObject> m_HoverGameObjects = new Dictionary<Transform, GameObject>();

        readonly Dictionary<Transform, GameObject> m_SelectionHoverGameObjects = new Dictionary<Transform, GameObject>();

        public ActionMap actionMap { get { return m_ActionMap; } }
        public bool ignoreLocking { get { return false; } }

        public Transform rayOrigin { private get; set; }
        public Node node { private get; set; }

        public Sprite icon { get { return m_Icon; } }

        public event Action<GameObject, Transform> hovered;

        public List<ILinkedObject> linkedObjects { get; set; }

        public string tooltipText { get { return m_MultiSelect ? "Multi-Select Enabled" : ""; } }
        public Transform tooltipTarget { get; private set; }
        public Transform tooltipSource { get { return rayOrigin; } }
        public TextAlignment tooltipAlignment { get { return TextAlignment.Center; } }

        void Start()
        {
            m_NormalRayColor = this.GetDefaultRayColor(rayOrigin);
            m_MultiselectRayColor = m_NormalRayColor;
            m_MultiselectRayColor = MaterialUtils.HueShift(m_MultiselectRayColor, k_MultiselectHueShift);

            tooltipTarget = ObjectUtils.CreateEmptyGameObject("SelectionTool Tooltip Target", rayOrigin).transform;
            tooltipTarget.localPosition = k_TooltipPosition;
            tooltipTarget.localRotation = k_TooltipRotation;

            InputUtils.GetBindingDictionaryFromActionMap(m_ActionMap, m_Controls);
        }

        void OnDestroy()
        {
            this.ClearFeedbackRequests();
        }

        public void ProcessInput(ActionMapInput input, ConsumeControlDelegate consumeControl)
        {
            if (this.GetManipulatorDragState())
                return;

            m_SelectionInput = (SelectionInput)input;

            var multiSelectControl = m_SelectionInput.multiSelect;
            if (this.GetDeviceType() == DeviceType.Vive)
                multiSelectControl = m_SelectionInput.multiSelectAlt;

            if (multiSelectControl.wasJustPressed)
            {
                var realTime = Time.realtimeSinceStartup;
                if (UIUtils.IsDoubleClick(realTime - m_LastMultiSelectClickTime))
                {
                    foreach (var linkedObject in linkedObjects)
                    {
                        var selectionTool = (SelectionTool)linkedObject;
                        selectionTool.m_MultiSelect = !selectionTool.m_MultiSelect;
                        this.HideTooltip(selectionTool);
                    }

                    if (m_MultiSelect)
                        this.ShowTooltip(this);
                }

                m_LastMultiSelectClickTime = realTime;
            }

            this.SetDefaultRayColor(rayOrigin, m_MultiSelect ? m_MultiselectRayColor : m_NormalRayColor);

            if (this.IsSharedUpdater(this))
            {
                this.SetManipulatorsVisible(this, !m_MultiSelect);

                m_SelectionHoverGameObjects.Clear();
                foreach (var linkedObject in linkedObjects)
                {
                    var selectionTool = (SelectionTool)linkedObject;
                    selectionTool.m_HasDirectHover = false; // Clear old hover state
                    var selectionRayOrigin = selectionTool.rayOrigin;

                    if (!selectionTool.IsRayActive())
                        continue;

                    var hover = this.GetFirstGameObject(selectionRayOrigin);

                    if (!selectionTool.GetSelectionCandidate(ref hover))
                        continue;

                    if (hover)
                    {
                        GameObject lastHover;
                        if (m_HoverGameObjects.TryGetValue(selectionRayOrigin, out lastHover) && lastHover != hover)
                            this.SetHighlight(lastHover, false, selectionRayOrigin);

                        m_SelectionHoverGameObjects[selectionRayOrigin] = hover;
                        m_HoverGameObjects[selectionRayOrigin] = hover;
                    }
                }

                var directSelection = this.GetDirectSelection();

                // Unset highlight old hovers
                k_TempHovers.Clear();
                foreach (var kvp in m_HoverGameObjects)
                {
                    k_TempHovers[kvp.Key] = kvp.Value;
                }

                foreach (var kvp in k_TempHovers)
                {
                    var directRayOrigin = kvp.Key;
                    var hover = kvp.Value;

                    if (!directSelection.ContainsKey(directRayOrigin)
                        && !m_SelectionHoverGameObjects.ContainsKey(directRayOrigin))
                    {
                        this.SetHighlight(hover, false, directRayOrigin);
                        m_HoverGameObjects.Remove(directRayOrigin);
                    }
                }

                // Find new hovers
                foreach (var kvp in directSelection)
                {
                    var directRayOrigin = kvp.Key;
                    var directHoveredObject = kvp.Value;

                    var directSelectionCandidate = this.GetSelectionCandidate(directHoveredObject, true);

                    // Can't select this object (it might be locked or static)
                    if (directHoveredObject && !directSelectionCandidate)
                        continue;

                    if (directSelectionCandidate)
                        directHoveredObject = directSelectionCandidate;

                    if (!this.CanGrabObject(directHoveredObject, directRayOrigin))
                        continue;

                    var grabbingNode = this.RequestNodeFromRayOrigin(directRayOrigin);
                    var selectionTool = linkedObjects.Cast<SelectionTool>().FirstOrDefault(linkedObject => linkedObject.node == grabbingNode);
                    if (selectionTool == null)
                        continue;

                    if (!selectionTool.IsDirectActive())
                    {
                        m_HoverGameObjects.Remove(directRayOrigin);
                        this.SetHighlight(directHoveredObject, false, directRayOrigin);
                        continue;
                    }

                    // Only overwrite an existing selection if it does not contain the hovered object
                    // In the case of multi-select, only add, do not remove
                    if (selectionTool.m_SelectionInput.select.wasJustPressed && !Selection.objects.Contains(directHoveredObject))
                        this.SelectObject(directHoveredObject, directRayOrigin, m_MultiSelect);

                    GameObject lastHover;
                    if (m_HoverGameObjects.TryGetValue(directRayOrigin, out lastHover) && lastHover != directHoveredObject)
                        this.SetHighlight(lastHover, false, directRayOrigin);

                    m_HoverGameObjects[directRayOrigin] = directHoveredObject;
                    selectionTool.m_HasDirectHover = true;
                }

                // Set highlight on new hovers
                foreach (var hover in m_HoverGameObjects)
                {
                    this.SetHighlight(hover.Value, true, hover.Key);
                }
            }

            if (!m_HasDirectHover)
                HideDirectSelectFeedback();
            else if (m_DirectSelectFeedback.Count == 0)
                ShowDirectSelectFeedback();

            if (!IsRayActive())
            {
                HideSelectFeedback();
                return;
            }

            // Need to call GetFirstGameObject a second time because we do not guarantee shared updater executes first
            var hoveredObject = this.GetFirstGameObject(rayOrigin);

            if (hovered != null)
                hovered(hoveredObject, rayOrigin);

            if (!GetSelectionCandidate(ref hoveredObject))
            {
                HideSelectFeedback();
                return;
            }

            if (!hoveredObject)
                HideSelectFeedback();
            else if (m_SelectFeedback.Count == 0)
                ShowSelectFeedback();

            // Capture object on press
            if (m_SelectionInput.select.wasJustPressed)
                m_PressedObject = hoveredObject;

            // Select button on release
            if (m_SelectionInput.select.wasJustReleased)
            {
                if (m_PressedObject == hoveredObject)
                {
                    this.SelectObject(m_PressedObject, rayOrigin, m_MultiSelect, true);
                    this.ResetDirectSelectionState();

                    if (m_PressedObject != null)
                        this.SetHighlight(m_PressedObject, false, rayOrigin);
                }

                if (m_PressedObject)
                    consumeControl(m_SelectionInput.select);

                m_PressedObject = null;
            }
        }

        bool GetSelectionCandidate(ref GameObject hoveredObject)
        {
            var selectionCandidate = this.GetSelectionCandidate(hoveredObject, true);

            // Can't select this object (it might be locked or static)
            if (hoveredObject && !selectionCandidate)
                return false;

            if (selectionCandidate)
                hoveredObject = selectionCandidate;

            return true;
        }

        bool IsDirectActive()
        {
            if (rayOrigin == null)
                return false;

            if (!this.IsConeVisible(rayOrigin))
                return false;

            if (this.IsInMiniWorld(rayOrigin))
                return true;

            if (this.IsMainMenuVisible(rayOrigin))
                return false;

            return true;
        }

        bool IsRayActive()
        {
            if (rayOrigin == null)
                return false;

            if (this.IsHoveringOverUI(rayOrigin))
                return false;

            if (this.IsMainMenuVisible(rayOrigin))
                return false;

            if (this.IsInMiniWorld(rayOrigin))
                return false;

            if (!this.IsRayVisible(rayOrigin))
                return false;

            return true;
        }

        void OnDisable()
        {
            foreach (var kvp in m_HoverGameObjects)
            {
                this.SetHighlight(kvp.Value, false, kvp.Key);
            }
            m_HoverGameObjects.Clear();
        }

        public void OnResetDirectSelectionState() { }

        void ShowFeedback(List<ProxyFeedbackRequest> requests, string controlName, string tooltipText = null)
        {
            if (tooltipText == null)
                tooltipText = controlName;

            List<VRInputDevice.VRControl> ids;
            if (m_Controls.TryGetValue(controlName, out ids))
            {
                foreach (var id in ids)
                {
                    var request = new ProxyFeedbackRequest
                    {
                        node = node,
                        control = id,
                        tooltipText = tooltipText
                    };

                    this.AddFeedbackRequest(request);
                    requests.Add(request);
                }
            }
        }

        void ShowSelectFeedback()
        {
            ShowFeedback(m_SelectFeedback, "Select");
        }

        void ShowDirectSelectFeedback()
        {
            ShowFeedback(m_DirectSelectFeedback, "Select", "Direct Select");
        }

        void HideFeedback(List<ProxyFeedbackRequest> requests)
        {
            foreach (var request in requests)
            {
                this.RemoveFeedbackRequest(request);
            }
            requests.Clear();
        }

        void HideSelectFeedback()
        {
            HideFeedback(m_SelectFeedback);
        }

        void HideDirectSelectFeedback()
        {
            HideFeedback(m_DirectSelectFeedback);
        }
    }
}
#endif
