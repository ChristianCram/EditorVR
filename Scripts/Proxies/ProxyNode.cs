﻿#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor.Experimental.EditorVR.Core;
using UnityEditor.Experimental.EditorVR.Utilities;
using UnityEngine;
using UnityEngine.InputNew;

namespace UnityEditor.Experimental.EditorVR.Proxies
{
    using VisibilityControlType = ProxyAffordanceMap.VisibilityControlType;
    using VRControl = VRInputDevice.VRControl;

    class ProxyNode : MonoBehaviour, ISetTooltipVisibility, ISetHighlight, IConnectInterfaces
    {
        class AffordanceData
        {
            class MaterialData
            {
                class VisibilityState
                {
                    readonly int m_MaterialIndex;
                    readonly AffordanceTooltip[] m_Tooltips;
                    readonly AffordanceVisibilityDefinition m_Definition;

                    bool m_Visibile;
                    float m_VisibilityChangeTime;

                    public int materialIndex { get { return m_MaterialIndex; } }
                    public AffordanceTooltip[] tooltips { get { return m_Tooltips; } }
                    public AffordanceVisibilityDefinition definition { get { return m_Definition; } }

                    public float visibleDuration { get; set; }
                    public float hideTime { get { return m_VisibilityChangeTime + visibleDuration; } }

                    public bool visible
                    {
                        get { return m_Visibile; }
                        set
                        {
                            m_VisibilityChangeTime = Time.time;
                            m_Visibile = value;
                        }
                    }

                    public VisibilityState(Renderer renderer, AffordanceTooltip[] tooltips, AffordanceVisibilityDefinition definition, Material material)
                    {
                        m_Tooltips = tooltips;
                        m_Definition = definition;
                        m_MaterialIndex = Array.IndexOf(renderer.sharedMaterials, material);
                    }
                }

                bool m_WasVisible;
                float m_VisibleChangeTime;
                Color m_OriginalColor;
                Color m_StartColor;

                readonly Dictionary<VRControl, VisibilityState> m_Visibilities = new Dictionary<VRControl, VisibilityState>();

                public void AddAffordance(Material material, VRControl control, Renderer renderer,
                    AffordanceTooltip[] tooltips, AffordanceVisibilityDefinition definition)
                {
                    if (m_Visibilities.ContainsKey(control))
                        Debug.LogWarning("multiple");

                    m_Visibilities[control] = new VisibilityState(renderer, tooltips, definition, material);

                    switch (definition.visibilityType)
                    {
                        case VisibilityControlType.AlphaProperty:
                            m_OriginalColor = material.GetFloat(definition.alphaProperty) * Color.white;
                            break;
                        case VisibilityControlType.ColorProperty:
                            m_OriginalColor = material.GetColor(definition.colorProperty);
                            break;
                    }

                    m_StartColor = m_OriginalColor;
                }

                public void Update(Renderer renderer, Material material, float time, float fadeInDuration, float fadeOutDuration,
                    ProxyNode proxyNode, AffordanceVisibilityDefinition visibilityOverride)
                {
                    var definition = visibilityOverride;
                    var hideTime = 0f;
                    if (definition == null)
                    {
                        foreach (var kvp in m_Visibilities)
                        {
                            var visibilityState = kvp.Value;
                            if (visibilityState.visible)
                            {
                                if (visibilityState.hideTime > hideTime)
                                {
                                    definition = visibilityState.definition;
                                    hideTime = visibilityState.visibleDuration > 0 ? visibilityState.hideTime : 0;
                                }
                            }
                        }
                    }

                    var visible = definition != null;
                    if (!visible)
                    {
                        foreach (var kvp in m_Visibilities)
                        {
                            definition = kvp.Value.definition;
                            break;
                        }
                    }

                    if (visible != m_WasVisible)
                        m_VisibleChangeTime = time;

                    var timeDiff = time - m_VisibleChangeTime;
                    var fadeDuration = visible ? fadeInDuration : fadeOutDuration;

                    switch (definition.visibilityType)
                    {
                        case VisibilityControlType.AlphaProperty:
                            var alphaProperty = definition.alphaProperty;
                            if (visible != m_WasVisible)
                                m_StartColor = material.GetFloat(alphaProperty) * Color.white;

                            var current = m_StartColor.a;
                            var target = visible ? m_OriginalColor.a : definition.hiddenColor.a;
                            if (!Mathf.Approximately(current, target))
                            {
                                var duration = current / target * fadeDuration;
                                var smoothedAmount = MathUtilsExt.SmoothInOutLerpFloat(timeDiff / duration);
                                if (smoothedAmount > 1)
                                {
                                    current = target;
                                    var color = m_StartColor;
                                    color.a = current;
                                    m_StartColor = color;
                                }
                                else
                                {
                                    current = Mathf.Lerp(current, target, smoothedAmount);
                                }

                                material.SetFloat(alphaProperty, current);
                            }
                            break;
                        case VisibilityControlType.ColorProperty:
                            var colorProperty = definition.colorProperty;
                            if (visible != m_WasVisible)
                                m_StartColor = material.GetColor(colorProperty);

                            var targetColor = visible ? m_OriginalColor : definition.hiddenColor;
                            if (m_StartColor != targetColor)
                            {
                                Color currentColor;
                                var duration = m_StartColor.grayscale / targetColor.grayscale * fadeDuration;
                                var smoothedAmount = MathUtilsExt.SmoothInOutLerpFloat(timeDiff / duration);
                                if (smoothedAmount > 1)
                                {
                                    m_StartColor = targetColor;
                                    currentColor = targetColor;
                                }
                                else
                                {
                                    currentColor = Color.Lerp(m_StartColor, targetColor, smoothedAmount);
                                }

                                material.SetColor(colorProperty, currentColor);
                            }
                            break;
                    }

                    if (visible != m_WasVisible)
                    {
                        foreach (var kvp in m_Visibilities)
                        {
                            var visibilityState = kvp.Value;
                            if (visibilityState.definition.visibilityType == VisibilityControlType.MaterialSwap)
                                renderer.sharedMaterials[visibilityState.materialIndex] =
                                    visible ? material : visibilityState.definition.hiddenMaterial;
                        }
                    }

                    m_WasVisible = visible;

                    if (visible && hideTime > 0 && Time.time > hideTime)
                    {
                        foreach (var kvp in m_Visibilities)
                        {
                            var visibilityState = kvp.Value;
                            var tooltips = visibilityState.tooltips;
                            if (tooltips != null)
                            {
                                foreach (var tooltip in tooltips)
                                {
                                    if (tooltip)
                                        proxyNode.HideTooltip(tooltip, true);
                                }
                            }

                            proxyNode.SetHighlight(renderer.gameObject, false);

                            visibilityState.visible = false;
                        }
                    }
                }

                public bool GetVisibility(VRControl control)
                {
                    foreach (var kvp in m_Visibilities)
                    {
                        if (kvp.Key != control)
                            continue;

                        if (kvp.Value.visible)
                            return true;
                    }

                    return false;
                }

                public void SetVisibility(bool visible, float duration, VRControl control)
                {
                    VisibilityState visibilityState;
                    if (m_Visibilities.TryGetValue(control, out visibilityState))
                    {
                        visibilityState.visible = visible;
                        visibilityState.visibleDuration = duration;
                    }
                }
            }

            readonly Dictionary<Material, MaterialData> m_MaterialDictionary = new Dictionary<Material, MaterialData>();

            public void AddAffordance(Affordance affordance, AffordanceVisibilityDefinition definition)
            {
                var control = affordance.control;
                var targetMaterial = affordance.material;
                var renderer = affordance.renderer;
                var tooltips = affordance.tooltips;
                if (targetMaterial != null)
                {
                    AddMaterialData(targetMaterial, control, renderer, tooltips, definition);
                }
                else
                {
                    foreach (var material in renderer.sharedMaterials)
                    {
                        AddMaterialData(material, control, renderer, tooltips, definition);
                    }
                }
            }

            public void AddRenderer(Renderer renderer, AffordanceVisibilityDefinition definition)
            {
                foreach (var material in renderer.sharedMaterials)
                {
                    AddMaterialData(material, default(VRControl), renderer, null, definition);
                }
            }

            void AddMaterialData(Material material, VRControl control, Renderer renderer, AffordanceTooltip[] tooltips,
                AffordanceVisibilityDefinition definition)
            {
                MaterialData materialData;
                if (!m_MaterialDictionary.TryGetValue(material, out materialData))
                {
                    materialData = new MaterialData();
                    m_MaterialDictionary[material] = materialData;
                }

                materialData.AddAffordance(material, control, renderer, tooltips, definition);
            }

            public void SetVisibility(bool visible, float duration = 0f, VRControl control = default(VRControl))
            {
                foreach (var kvp in m_MaterialDictionary)
                {
                    kvp.Value.SetVisibility(visible, duration, control);
                }
            }

            public bool GetVisibility(VRControl control = default(VRControl))
            {
                foreach (var kvp in m_MaterialDictionary)
                {
                    if (kvp.Value.GetVisibility(control))
                        return true;
                }

                return false;
            }

            public void Update(Renderer renderer, float time, float fadeInDuration, float fadeOutDuration,
                ProxyNode proxyNode, AffordanceVisibilityDefinition visibilityOverride = null)
            {
                foreach (var kvp in m_MaterialDictionary)
                {
                    kvp.Value.Update(renderer, kvp.Key, time, fadeInDuration, fadeOutDuration, proxyNode, visibilityOverride);
                }
            }

            public void OnDestroy()
            {
                foreach (var kvp in m_MaterialDictionary)
                {
                    ObjectUtils.Destroy(kvp.Key);
                }
            }
        }

        /// <summary>
        /// Used as globally unique identifiers for feedback requests
        /// They are used to relate feedback requests to the persistent count of visible presentations used to suppress feedback
        /// </summary>
        [Serializable]
        internal struct RequestKey
        {
            /// <summary>
            /// The control index used to identify the related affordance
            /// </summary>
            [SerializeField]
            VRControl m_Control;

            /// <summary>
            /// The tooltip text that was presented
            /// </summary>
            [SerializeField]
            string m_TooltipText;

            public RequestKey(ProxyFeedbackRequest request)
            {
                m_Control = request.control;
                m_TooltipText = request.tooltipText;
            }

            public override int GetHashCode()
            {
                var hashCode = (int)m_Control;

                if (m_TooltipText != null)
                    hashCode ^= m_TooltipText.GetHashCode();

                return hashCode;
            }
        }

        /// <summary>
        /// Contains per-request persistent data
        /// </summary>
        [Serializable]
        internal class RequestData
        {
            [SerializeField]
            int m_Presentations;

            /// <summary>
            /// How many times the user viewed the presentation of this type of request
            /// </summary>
            public int presentations
            {
                get { return m_Presentations; }
                set { m_Presentations = value; }
            }

            public bool visibleThisPresentation { get; set; }
        }

        /// <summary>
        /// Used to store persistent data about feedback requests
        /// </summary>
        [Serializable]
        internal class SerializedFeedback
        {
            public RequestKey[] keys;
            public RequestData[] values;
        }

        const string k_ZWritePropertyName = "_ZWrite";

        static readonly ProxyFeedbackRequest k_ShakeFeedbackRequest = new ProxyFeedbackRequest { showBody = true };

        [SerializeField]
        float m_FadeInDuration = 0.5f;

        [SerializeField]
        float m_FadeOutDuration = 2f;

        [SerializeField]
        Transform m_RayOrigin;

        [SerializeField]
        Transform m_MenuOrigin;

        [SerializeField]
        Transform m_AlternateMenuOrigin;

        [SerializeField]
        Transform m_PreviewOrigin;

        [SerializeField]
        Transform m_FieldGrabOrigin;

        [SerializeField]
        Transform m_NaturalOrientation;

        [SerializeField]
        ProxyAnimator m_ProxyAnimator;

        [SerializeField]
        ProxyAffordanceMap m_AffordanceMap;

        [HideInInspector]
        [SerializeField]
        Material m_ProxyBackgroundMaterial;

        [Tooltip("Affordance objects that store transform, renderer, and tooltip references")]
        [SerializeField]
        Affordance[] m_Affordances;

        readonly Dictionary<Renderer, AffordanceData> m_AffordanceData = new Dictionary<Renderer, AffordanceData>();
        readonly List<Tuple<Renderer, AffordanceData>> m_BodyData = new List<Tuple<Renderer, AffordanceData>>();

        FacingDirection m_FacingDirection = FacingDirection.Back;

        SerializedFeedback m_SerializedFeedback;
        readonly List<ProxyFeedbackRequest> m_FeedbackRequests = new List<ProxyFeedbackRequest>();
        readonly Dictionary<RequestKey, RequestData> m_RequestData = new Dictionary<RequestKey, RequestData>();

        // Local method use only -- created here to reduce garbage collection
        static readonly List<ProxyFeedbackRequest> k_FeedbackRequestsCopy = new List<ProxyFeedbackRequest>();

        /// <summary>
        /// The transform that the device's ray contents (default ray, custom ray, etc) will be parented under
        /// </summary>
        public Transform rayOrigin { get { return m_RayOrigin; } }

        /// <summary>
        /// The transform that the menu content will be parented under
        /// </summary>
        public Transform menuOrigin { get { return m_MenuOrigin; } }

        /// <summary>
        /// The transform that the alternate-menu content will be parented under
        /// </summary>
        public Transform alternateMenuOrigin { get { return m_AlternateMenuOrigin; } }

        /// <summary>
        /// The transform that the display/preview objects will be parented under
        /// </summary>
        public Transform previewOrigin { get { return m_PreviewOrigin; } }

        /// <summary>
        /// The transform that the display/preview objects will be parented under
        /// </summary>
        public Transform fieldGrabOrigin { get { return m_FieldGrabOrigin; } }

        void Awake()
        {
            // Don't allow setup if affordances are invalid
            if (m_Affordances == null || m_Affordances.Length == 0)
            {
                Debug.LogError("Affordances invalid when attempting to setup ProxyUI on : " + gameObject.name);
                return;
            }

            // Prevent further setup if affordance map isn't assigned
            if (m_AffordanceMap == null)
            {
                Debug.LogError("A valid Affordance Map must be present when setting up ProxyUI on : " + gameObject.name);
                return;
            }

            var affordanceDefinitions = new AffordanceDefinition[m_Affordances.Length];
            var affordanceMapDefinitions = m_AffordanceMap.AffordanceDefinitions;
            var defaultAffordanceVisibilityDefinition = m_AffordanceMap.defaultAffordanceVisibilityDefinition;
            var defaultAffordanceAnimationDefinition = m_AffordanceMap.defaultAnimationDefinition;
            for (var i = 0; i < m_Affordances.Length; i++)
            {
                var affordance = m_Affordances[i];
                var renderer = affordance.renderer;
                var sharedMaterials = renderer.sharedMaterials;
                AffordanceData affordanceData;
                if (!m_AffordanceData.TryGetValue(renderer, out affordanceData))
                {
                    MaterialUtils.CloneMaterials(renderer); // Clone all materials associated with each renderer once
                    affordanceData = new AffordanceData();
                    m_AffordanceData[renderer] = affordanceData;

                    // Clones that utilize the standard shader can lose their enabled ZWrite value (1), if it was enabled on the material
                    foreach (var material in sharedMaterials)
                    {
                        material.SetFloat(k_ZWritePropertyName, 1);
                    }
                }

                var control = affordance.control;
                var definition = affordanceMapDefinitions.FirstOrDefault(x => x.control == control);
                if (definition == null)
                {
                    definition = new AffordanceDefinition
                    {
                        control = control,
                        visibilityDefinition = defaultAffordanceVisibilityDefinition,
                        animationDefinition = defaultAffordanceAnimationDefinition
                    };
                }

                affordanceDefinitions[i] = definition;
                affordanceData.AddAffordance(affordance, definition.visibilityDefinition);
            }

            foreach (var kvp in m_AffordanceData)
            {
                kvp.Key.AddMaterial(m_ProxyBackgroundMaterial);
            }

            var bodyRenderers = GetComponentsInChildren<Renderer>(true)
                .Where(x => !m_AffordanceData.ContainsKey(x) && !IsChildOfProxyOrigin(x.transform)).ToList();

            var bodyAffordanceDefinition = new AffordanceDefinition
            {
                visibilityDefinition = m_AffordanceMap.bodyVisibilityDefinition
            };

            foreach (var renderer in bodyRenderers)
            {
                MaterialUtils.CloneMaterials(renderer);
                var affordanceData = new AffordanceData();
                m_BodyData.Add(new Tuple<Renderer, AffordanceData>(renderer, affordanceData));
                affordanceData.AddRenderer(renderer, bodyAffordanceDefinition.visibilityDefinition);
                renderer.AddMaterial(m_ProxyBackgroundMaterial);
            }

            if (m_ProxyAnimator)
                m_ProxyAnimator.Setup(affordanceDefinitions, m_Affordances);
        }

        void Start()
        {
            if (m_ProxyAnimator)
                this.ConnectInterfaces(m_ProxyAnimator, rayOrigin);

            AddFeedbackRequest(k_ShakeFeedbackRequest);
        }

        void Update()
        {
            var cameraPosition = CameraUtils.GetMainCamera().transform.position;
            var direction = GetFacingDirection(cameraPosition);
            if (m_FacingDirection != direction)
            {
                m_FacingDirection = direction;
                UpdateFacingDirection(direction);
            }

            AffordanceVisibilityDefinition bodyVisibility = null;
            foreach (var tuple in m_BodyData)
            {
                if (tuple.secondElement.GetVisibility())
                {
                    bodyVisibility = m_AffordanceMap.bodyVisibilityDefinition;
                    break;
                }
            }

            var time = Time.time;

            foreach (var kvp in m_AffordanceData)
            {
                kvp.Value.Update(kvp.Key, time, m_FadeInDuration, m_FadeOutDuration, this, bodyVisibility);
            }

            foreach (var tuple in m_BodyData)
            {
                tuple.secondElement.Update(tuple.firstElement, time, m_FadeInDuration, m_FadeOutDuration, this);
            }
        }

        void OnDestroy()
        {
            StopAllCoroutines();

            foreach (var kvp in m_AffordanceData)
            {
                kvp.Value.OnDestroy();
            }

            foreach (var tuple in m_BodyData)
            {
                ObjectUtils.Destroy(tuple.firstElement);
            }
        }

        FacingDirection GetFacingDirection(Vector3 cameraPosition)
        {
            var toCamera = Vector3.Normalize(cameraPosition - m_NaturalOrientation.position);

            var xDot = Vector3.Dot(toCamera, m_NaturalOrientation.right);
            var yDot = Vector3.Dot(toCamera, m_NaturalOrientation.up);
            var zDot = Vector3.Dot(toCamera, m_NaturalOrientation.forward);

            if (Mathf.Abs(xDot) > Mathf.Abs(yDot))
            {
                if (Mathf.Abs(zDot) > Mathf.Abs(xDot))
                    return zDot > 0 ? FacingDirection.Front : FacingDirection.Back;

                return xDot > 0 ? FacingDirection.Right : FacingDirection.Left;
            }

            if (Mathf.Abs(zDot) > Mathf.Abs(yDot))
                return zDot > 0 ? FacingDirection.Front : FacingDirection.Back;

            return yDot > 0 ? FacingDirection.Top : FacingDirection.Bottom;
        }

        void UpdateFacingDirection(FacingDirection direction)
        {
            foreach (var request in m_FeedbackRequests)
            {
                foreach (var affordance in m_Affordances)
                {
                    if (affordance.control != request.control)
                        continue;

                    foreach (var tooltip in affordance.tooltips)
                    {
                        // Only update placement, do not affect duration
                        this.ShowTooltip(tooltip, true, -1, tooltip.GetPlacement(direction));
                    }
                }
            }
        }

        bool IsChildOfProxyOrigin(Transform transform)
        {
            if (transform.IsChildOf(rayOrigin))
                return true;

            if (transform.IsChildOf(menuOrigin))
                return true;

            if (transform.IsChildOf(alternateMenuOrigin))
                return true;

            if (transform.IsChildOf(previewOrigin))
                return true;

            if (transform.IsChildOf(fieldGrabOrigin))
                return true;

            return false;
        }

        public void AddShakeRequest()
        {
            RemoveFeedbackRequest(k_ShakeFeedbackRequest);
            AddFeedbackRequest(k_ShakeFeedbackRequest);
        }

        public void AddFeedbackRequest(ProxyFeedbackRequest request)
        {
            m_FeedbackRequests.Add(request);
            ExecuteFeedback(request);
        }

        void ExecuteFeedback(ProxyFeedbackRequest changedRequest)
        {
            if (!isActiveAndEnabled)
                return;

            if (changedRequest.showBody)
            {
                foreach (var tuple in m_BodyData)
                {
                    tuple.secondElement.SetVisibility(true, changedRequest.duration);
                }
                return;
            }

            ProxyFeedbackRequest request = null;
            foreach (var feedbackRequest in m_FeedbackRequests)
            {
                if (feedbackRequest.control != changedRequest.control || feedbackRequest.showBody != changedRequest.showBody)
                    continue;

                if (request == null || feedbackRequest.priority >= request.priority)
                    request = feedbackRequest;
            }

            if (request == null)
                return;

            var feedbackKey = new RequestKey(request);
            RequestData data;
            if (!m_RequestData.TryGetValue(feedbackKey, out data))
            {
                data = new RequestData();
                m_RequestData[feedbackKey] = data;
            }

            var suppress = data.presentations > request.maxPresentations - 1;
            var suppressPresentation = request.suppressPresentation;
            if (suppressPresentation != null)
                suppress = suppressPresentation();

            if (suppress)
                return;

            foreach (var affordance in m_Affordances)
            {
                if (affordance.control != request.control)
                    continue;

                m_AffordanceData[affordance.renderer].SetVisibility(!request.suppressExisting, request.duration, changedRequest.control);

                this.SetHighlight(affordance.renderer.gameObject, !request.suppressExisting);

                var tooltipText = request.tooltipText;
                if (!string.IsNullOrEmpty(tooltipText) || request.suppressExisting)
                {
                    foreach (var tooltip in affordance.tooltips)
                    {
                        if (tooltip)
                        {
                            data.visibleThisPresentation = false;
                            tooltip.tooltipText = tooltipText;
                            this.ShowTooltip(tooltip, true, placement: tooltip.GetPlacement(m_FacingDirection),
                                becameVisible: () =>
                                {
                                    if (!data.visibleThisPresentation)
                                        data.presentations++;

                                    data.visibleThisPresentation = true;
                                });
                        }
                    }
                }
            }
        }

        public void RemoveFeedbackRequest(ProxyFeedbackRequest request)
        {
            foreach (var affordance in m_Affordances)
            {
                if (affordance.control != request.control)
                    continue;

                m_AffordanceData[affordance.renderer].SetVisibility(false, request.duration, request.control);

                this.SetHighlight(affordance.renderer.gameObject, false);

                if (!string.IsNullOrEmpty(request.tooltipText))
                {
                    foreach (var tooltip in affordance.tooltips)
                    {
                        if (tooltip)
                        {
                            tooltip.tooltipText = string.Empty;
                            this.HideTooltip(tooltip, true);
                        }
                    }
                }
            }

            foreach (var feedbackRequest in m_FeedbackRequests)
            {
                if (feedbackRequest == request)
                {
                    m_FeedbackRequests.Remove(feedbackRequest);
                    if (!request.showBody)
                        ExecuteFeedback(request);

                    break;
                }
            }
        }

        public void ClearFeedbackRequests(IRequestFeedback caller)
        {
            k_FeedbackRequestsCopy.Clear();
            foreach (var request in m_FeedbackRequests)
            {
                if (request.caller == caller)
                    k_FeedbackRequestsCopy.Add(request);
            }

            foreach (var request in k_FeedbackRequestsCopy)
            {
                RemoveFeedbackRequest(request);
            }
        }

        public SerializedFeedback OnSerializePreferences()
        {
            if (m_SerializedFeedback != null)
            {
                var count = m_RequestData.Count;
                var keys = new RequestKey[count];
                var values = new RequestData[count];
                count = 0;
                foreach (var kvp in m_RequestData)
                {
                    keys[count] = kvp.Key;
                    values[count] = kvp.Value;
                    count++;
                }

                m_SerializedFeedback.keys = keys;
                m_SerializedFeedback.values = values;
            }

            return m_SerializedFeedback;
        }

        public void OnDeserializePreferences(object obj)
        {
            if (obj == null)
                return;

            m_SerializedFeedback = (SerializedFeedback)obj;
            if (m_SerializedFeedback.keys == null)
                return;

            var length = m_SerializedFeedback.keys.Length;
            var keys = m_SerializedFeedback.keys;
            var values = m_SerializedFeedback.values;
            for (var i = 0; i < length; i++)
            {
                m_RequestData[keys[i]] = values[i];
            }
        }
    }
}
#endif
