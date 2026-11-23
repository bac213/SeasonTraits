using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace SeasonTraits.Client;

/// <summary>
/// Read-only summary of the current profile's season traits, embedded in the character screen as a
/// third sub-tab next to Skills / Mastering. Built in code so no prefab or bundle changes are needed.
/// </summary>
public class TraitsSummaryPanel : MonoBehaviour
{
    // Same palette family as TraitsPanelUI, so the tab looks native next to Skills/Mastering.
    private static readonly Color PanelBg = new Color(0.055f, 0.063f, 0.073f, 0.92f);
    private static readonly Color RowBg = new Color(0.163f, 0.176f, 0.196f, 0.94f);
    private static readonly Color Gold = new Color(0.780f, 0.663f, 0.420f);
    private static readonly Color TextMain = new Color(0.851f, 0.878f, 0.910f);
    private static readonly Color TextDim = new Color(0.62f, 0.65f, 0.68f);
    private static readonly Color TextPos = new Color(0.561f, 0.749f, 0.910f);
    private static readonly Color TextNeg = new Color(0.910f, 0.541f, 0.478f);
    private static readonly Color Green = new Color(0.549f, 0.784f, 0.549f);
    private static readonly Color Red = new Color(0.90f, 0.40f, 0.35f);

    private const float RowHeight = 84f;

    private bool _built;
    private RectTransform _content;
    private TMP_Text _title;
    private TMP_Text _balance;
    private TMP_Text _empty;
    private readonly List<GameObject> _rows = new List<GameObject>();

    private static TMP_FontAsset _font;

    /// <summary>Creates the panel under <paramref name="parent"/>, mirroring the RectTransform of <paramref name="template"/>.</summary>
    public static TraitsSummaryPanel Create(Transform parent, RectTransform template)
    {
        var host = new GameObject("SeasonTraitsSummaryPanel", typeof(RectTransform));
        host.transform.SetParent(parent, false);

        var rt = (RectTransform)host.transform;
        if (template != null)
        {
            rt.anchorMin = template.anchorMin;
            rt.anchorMax = template.anchorMax;
            rt.pivot = template.pivot;
            rt.offsetMin = template.offsetMin;
            rt.offsetMax = template.offsetMax;
        }
        else
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }

        var panel = host.AddComponent<TraitsSummaryPanel>();
        host.SetActive(false);
        return panel;
    }

    public void ShowPanel()
    {
        EnsureBuilt();
        gameObject.SetActive(true);
        Refresh();
    }

    public void HidePanel()
    {
        if (this != null)
        {
            gameObject.SetActive(false);
        }
    }

    private void EnsureBuilt()
    {
        if (_built)
        {
            if (_font == null)
            {
                _font = TraitsPanelUI.ResolveTmpFont();
            }
            return;
        }

        _font = TraitsPanelUI.ResolveTmpFont();
        Build();
        _built = true;
    }

    private void Build()
    {
        var root = (RectTransform)transform;

        var background = root.gameObject.AddComponent<Image>();
        background.color = PanelBg;
        background.raycastTarget = false;

        _title = NewTmp("Title", root, "已选特质", 21f, Gold, FontStyles.Bold);
        Place(_title.rectTransform, new Vector2(0f, 1f), new Vector2(320f, 30f), new Vector2(20f, -14f), new Vector2(0f, 1f));

        _balance = NewTmp("Balance", root, "", 16f, TextMain, FontStyles.Bold);
        Place(_balance.rectTransform, new Vector2(1f, 1f), new Vector2(300f, 26f), new Vector2(-20f, -16f), new Vector2(1f, 1f));
        _balance.alignment = TextAlignmentOptions.MidlineRight;

        var separator = NewRect("Separator", root);
        Anchor(separator, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(20f, -50f), new Vector2(-20f, -49f), new Vector2(0.5f, 1f));
        var separatorImage = separator.gameObject.AddComponent<Image>();
        separatorImage.color = new Color(1f, 1f, 1f, 0.08f);
        separatorImage.raycastTarget = false;

        var scrollRoot = NewRect("Scroll", root);
        scrollRoot.anchorMin = Vector2.zero;
        scrollRoot.anchorMax = Vector2.one;
        scrollRoot.offsetMin = new Vector2(16f, 16f);
        scrollRoot.offsetMax = new Vector2(-16f, -58f);

        var viewport = NewRect("Viewport", scrollRoot);
        viewport.anchorMin = Vector2.zero;
        viewport.anchorMax = Vector2.one;
        viewport.offsetMin = Vector2.zero;
        viewport.offsetMax = Vector2.zero;
        var viewportImage = viewport.gameObject.AddComponent<Image>();
        viewportImage.color = new Color(0f, 0f, 0f, 0.001f);
        viewportImage.raycastTarget = true;
        viewport.gameObject.AddComponent<RectMask2D>();

        _content = NewRect("Content", viewport);
        _content.anchorMin = new Vector2(0f, 1f);
        _content.anchorMax = new Vector2(1f, 1f);
        _content.pivot = new Vector2(0.5f, 1f);
        _content.anchoredPosition = Vector2.zero;
        _content.sizeDelta = Vector2.zero;

        var layout = _content.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.padding = new RectOffset(0, 0, 0, 0);
        layout.spacing = 8f;
        layout.childAlignment = TextAnchor.UpperCenter;
        layout.childControlWidth = true;
        layout.childControlHeight = true;
        layout.childForceExpandWidth = true;
        layout.childForceExpandHeight = false;

        var fitter = _content.gameObject.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;

        var scroll = scrollRoot.gameObject.AddComponent<ScrollRect>();
        scroll.content = _content;
        scroll.viewport = viewport;
        scroll.horizontal = false;
        scroll.vertical = true;
        scroll.movementType = ScrollRect.MovementType.Clamped;
        scroll.scrollSensitivity = 30f;
        scroll.inertia = true;

        _empty = NewTmp("Empty", root, "尚未选择特质\n在主页完成特质选择后，这里会显示已选特质。", 16f, TextDim, FontStyles.Normal);
        Place(_empty.rectTransform, new Vector2(0.5f, 0.5f), new Vector2(720f, 90f), Vector2.zero, new Vector2(0.5f, 0.5f));
        _empty.alignment = TextAlignmentOptions.Center;
        _empty.gameObject.SetActive(false);
    }

    private void Refresh()
    {
        if (!_built)
        {
            return;
        }

        ClearRows();

        var selection = TraitsApi.GetSelection();
        if (selection == null)
        {
            SetEmpty("无法读取特质数据\n服务器没有响应，稍后再试。");
            return;
        }

        var catalog = TraitsApi.GetCatalog();
        if (catalog == null)
        {
            SetEmpty("无法读取特质数据\n特质目录拉取失败，稍后再试。");
            return;
        }

        // A selection id the cached catalog doesn't know usually means the catalog changed under us.
        if (selection.Ids.Any(id => TraitsApi.FindTrait(catalog, id) == null))
        {
            var refreshed = TraitsApi.GetCatalog(force: true);
            if (refreshed != null)
            {
                catalog = refreshed;
            }
        }

        // Keep the in-raid effect state in sync with whatever the server says right now.
        TraitEffectsState.SetSelected(selection.Ids);

        var traits = selection.Ids
            .Select(id => TraitsApi.FindTrait(catalog, id) ?? FallbackTrait(id))
            .ToList();

        traits = traits
            .OrderBy(t => t.Value<string>("kind") == "negative" ? 1 : 0)
            .ThenByDescending(t => t.Value<int?>("points") ?? 0)
            .ToList();

        var balance = traits.Sum(t => t.Value<string>("kind") == "negative"
            ? t.Value<int?>("points") ?? 0
            : -(t.Value<int?>("points") ?? 0));

        _title.text = traits.Count > 0 ? $"已选特质 ({traits.Count})" : "已选特质";
        _balance.text = $"净点数 {(balance >= 0 ? "+" : "")}{balance}";
        _balance.color = balance >= 0 ? Green : Red;

        if (traits.Count == 0)
        {
            SetEmpty(selection.Locked
                ? "这个档案没有可显示的特质。"
                : "尚未选择特质\n在主页完成特质选择后，这里会显示已选特质。");
            return;
        }

        _empty.gameObject.SetActive(false);
        foreach (var trait in traits)
        {
            BuildRow(trait);
        }

        SeasonTraitsPlugin.Log.LogInfo($"SeasonTraits: traits tab rendered {traits.Count} trait(s), balance {balance}");
    }

    private void SetEmpty(string text)
    {
        _empty.text = text;
        _empty.gameObject.SetActive(true);
        _balance.text = "";
    }

    private void ClearRows()
    {
        foreach (var row in _rows)
        {
            if (row != null)
            {
                // Unparent first so the layout group stops measuring the row before it is destroyed.
                row.transform.SetParent(null, false);
                Destroy(row);
            }
        }
        _rows.Clear();
    }

    private void BuildRow(JObject trait)
    {
        var kind = trait.Value<string>("kind") ?? "positive";
        var negative = kind == "negative";
        var points = trait.Value<int?>("points") ?? 0;

        var rowGo = new GameObject("TraitRow", typeof(RectTransform));
        rowGo.transform.SetParent(_content, false);

        var rowImage = rowGo.AddComponent<Image>();
        rowImage.color = RowBg;
        rowImage.raycastTarget = true;

        var element = rowGo.AddComponent<LayoutElement>();
        element.minHeight = RowHeight;
        element.preferredHeight = RowHeight;

        var iconRect = NewRect("Icon", rowGo.transform);
        Place(iconRect, new Vector2(0f, 0.5f), new Vector2(48f, 48f), new Vector2(16f, 0f), new Vector2(0f, 0.5f));
        var icon = iconRect.gameObject.AddComponent<Image>();
        icon.sprite = TraitsPanelUI.GetTraitIcon(trait.Value<string>("icon"));
        icon.preserveAspect = true;
        icon.raycastTarget = false;
        if (icon.sprite == null)
        {
            icon.color = new Color(1f, 1f, 1f, 0.06f);
        }

        var name = NewTmp("Name", rowGo.transform, trait.Value<string>("nameZh") ?? trait.Value<string>("nameEn") ?? "?", 18f,
            negative ? TextNeg : TextPos, FontStyles.Bold);
        Anchor(name.rectTransform, new Vector2(0f, 1f), new Vector2(1f, 1f), new Vector2(76f, -32f), new Vector2(-150f, -8f), new Vector2(0f, 1f));
        name.overflowMode = TextOverflowModes.Ellipsis;

        var pointsText = NewTmp("Points", rowGo.transform, $"{(negative ? "负面" : "正面")}  {(negative ? "+" : "-")}{points}", 15f,
            negative ? Green : Gold, FontStyles.Bold);
        Place(pointsText.rectTransform, new Vector2(1f, 1f), new Vector2(134f, 24f), new Vector2(-16f, -10f), new Vector2(1f, 1f));
        pointsText.alignment = TextAlignmentOptions.MidlineRight;

        var desc = NewTmp("Desc", rowGo.transform, trait.Value<string>("descriptionZh") ?? trait.Value<string>("descriptionEn") ?? "", 14f, TextDim, FontStyles.Normal);
        Anchor(desc.rectTransform, new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(76f, 10f), new Vector2(-16f, -34f), new Vector2(0f, 0f));
        desc.alignment = TextAlignmentOptions.TopLeft;
        desc.enableWordWrapping = true;

        _rows.Add(rowGo);
    }

    private static JObject FallbackTrait(string id)
    {
        return new JObject
        {
            ["id"] = id,
            ["nameZh"] = id,
            ["kind"] = "positive",
            ["points"] = 0,
            ["descriptionZh"] = "服务端找不到这条特质的定义。",
        };
    }

    private static TMP_Text NewTmp(string name, Transform parent, string text, float size, Color color, FontStyles style)
    {
        var rect = NewRect(name, parent);
        var label = rect.gameObject.AddComponent<TextMeshProUGUI>();
        label.font = ResolveFont();
        label.fontSize = size;
        label.fontStyle = style;
        label.text = text ?? "";
        label.color = color;
        label.raycastTarget = false;
        label.alignment = TextAlignmentOptions.TopLeft;
        label.enableWordWrapping = true;
        label.overflowMode = TextOverflowModes.Overflow;
        return label;
    }

    private static TMP_FontAsset ResolveFont()
    {
        if (_font != null)
        {
            return _font;
        }

        _font = TraitsPanelUI.ResolveTmpFont();
        if (_font == null)
        {
            try
            {
                _font = TMP_Settings.defaultFontAsset;
            }
            catch
            {
                // ignored - TMP will fall back to its own default
            }
        }
        return _font;
    }

    private static RectTransform NewRect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return (RectTransform)go.transform;
    }

    private static void Anchor(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 offsetMin, Vector2 offsetMax, Vector2 pivot)
    {
        rect.anchorMin = anchorMin;
        rect.anchorMax = anchorMax;
        rect.pivot = pivot;
        rect.offsetMin = offsetMin;
        rect.offsetMax = offsetMax;
    }

    /// <summary>Fixed-size placement: anchor at a corner, size in pixels, offset from that anchor.</summary>
    private static void Place(RectTransform rect, Vector2 anchor, Vector2 size, Vector2 position, Vector2 pivot)
    {
        rect.anchorMin = anchor;
        rect.anchorMax = anchor;
        rect.pivot = pivot;
        rect.sizeDelta = size;
        rect.anchoredPosition = position;
    }
}
