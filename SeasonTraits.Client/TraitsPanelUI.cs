using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEngine.EventSystems;
using UnityEngine;
using UnityEngine.UI;

namespace SeasonTraits.Client;

/// <summary>
/// One-shot uGUI trait selection panel, official-style dark theme, built entirely in code.
/// Rows have three visual states (normal / hover / selected) plus conflict-dimmed state.
/// Live balance and mutual-exclusion validation mirror the server rules.
/// Public API unchanged: Create(JArray traits, Func&lt;JArray,string&gt; apply, Action closed).
/// </summary>
public class TraitsPanelUI : MonoBehaviour
{
    // ---- official-style palette (sampled from in-game dark UI) ----
    private static readonly Color PanelBg = new(0.075f, 0.085f, 0.100f, 0.97f);
    private static readonly Color RowBg = new(0.163f, 0.176f, 0.196f);          // #2A2D31
    private static readonly Color RowHover = new(0.212f, 0.231f, 0.259f);
    private static readonly Color RowSelected = new(0.227f, 0.353f, 0.478f);    // #3A5A7A
    private static readonly Color Gold = new(0.780f, 0.663f, 0.420f);           // #C7A96B
    private static readonly Color TextMain = new(0.851f, 0.878f, 0.910f);
    private static readonly Color TextDim = new(0.62f, 0.65f, 0.68f);
    private static readonly Color TextPos = new(0.561f, 0.749f, 0.910f);        // #8FBFE8
    private static readonly Color TextNeg = new(0.910f, 0.541f, 0.478f);        // #E88A7A
    private static readonly Color Green = new(0.549f, 0.784f, 0.549f);
    private static readonly Color Red = new(0.90f, 0.40f, 0.35f);
    private class RowState
    {
        public string Id;
        public JToken Trait;
        public Image Bg;
        public Image Bar;
        public Image Icon;
        public Image CheckMark;
        public Button RowButton;
        public Text NameText;
        public TMPro.TMP_Text NameTmp; // game-font label on official rows; NameText is the fallback
        public Text PointsText;
        public TMPro.TMP_Text PointsTmp;
        public Text DescText;
        public TMPro.TMP_Text DescTmp;
        public bool Selected;
        public bool Hovered;
        public bool Blocked;
        public Toggle OfficialToggle;
        public Color BaseColor; // positive/negative tint before hover/selection
        public RectTransform RowRoot; // official prefab clone; null for self-drawn rows
    }

    // ---- official control prefabs (probed at runtime; self-drawn fallback when missing) ----
    private static GameObject _buttonPrefab;
    private static GameObject _togglePrefab;
    private static GameObject _borderPrefab;
    private static readonly Dictionary<string, Sprite> _iconCache = new();

    private JArray _traits;
    private Func<JArray, string> _apply;
    private Action _onClosed;
    private bool _closed;
    private Transform _canvasRoot;
    private GameObject _confirmDialog;
    private EFT.UI.DialogWindowContext _officialConfirmContext;
    private bool _panelHiddenForConfirm;
    private static TMPro.TMP_FontAsset _tmpFont;

    private readonly Dictionary<string, RowState> _rows = new();
    private TMPro.TMP_Text _balanceText;
    private TMPro.TMP_Text _errorText;
    private TMPro.TMP_Text _centerHeaderText;
    private RectTransform _centerContent;
    private readonly List<RectTransform> _scrollContents = new();
    private Font _font;

    internal static TraitsPanelUI Active;

    public static TraitsPanelUI Create(JArray traits, Func<JArray, string> apply, Action closed)
    {
        var host = new GameObject("SeasonTraitsPanelHost");
        DontDestroyOnLoad(host);
        var panel = host.AddComponent<TraitsPanelUI>();
        panel._traits = traits;
        panel._apply = apply;
        panel._onClosed = closed;
        panel.Build();
        Active = panel;
        return panel;
    }


    /// <summary>Dirty every row graphic so RectMask2D re-culls after the content moved.</summary>
    private void RepaintRows()
    {
        foreach (var row in _rows.Values)
        {
            if (row.RowRoot != null)
            {
                foreach (var graphic in row.RowRoot.GetComponentsInChildren<Graphic>(true))
                {
                    graphic.SetVerticesDirty();
                    graphic.SetMaterialDirty();
                }
                LayoutRebuilder.MarkLayoutForRebuild(row.RowRoot);
            }
            else if (row.Bg != null)
            {
                row.Bg.SetVerticesDirty();
                row.Bg.SetMaterialDirty();
            }
        }
        if (_centerContent != null)
        {
            foreach (var graphic in _centerContent.GetComponentsInChildren<Graphic>(true))
            {
                graphic.SetVerticesDirty();
                graphic.SetMaterialDirty();
            }
            LayoutRebuilder.MarkLayoutForRebuild(_centerContent);
        }
    }
    /// <summary>Re-show the panel if the user left to another scene and came back.</summary>
    public void EnsureVisible()
    {
        if (_closed || gameObject.activeSelf)
        {
            return;
        }
        gameObject.SetActive(true);
    }

    private void Update()
    {
        if (_closed)
        {
            return;
        }
        if (_officialConfirmContext != null)
        {
            // the game's own confirm window handles ESC/Y/N and closes itself; OnClose clears the ref
            return;
        }
        if (!Input.GetKeyDown(KeyCode.Escape))
        {
            return;
        }
        if (_confirmDialog != null && _confirmDialog.activeSelf)
        {
            HideConfirm();
            return;
        }
        Close();
    }

    private void Build()
    {
        var canvasGo = new GameObject("SeasonTraitsCanvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasGo.transform.SetParent(transform, false);
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 30000; // above the menu's own canvases, which load after the panel
        var scaler = canvasGo.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1600, 900);
        _canvasRoot = canvasGo.transform;

        EnsureEventSystem();
        _font = FindGameFont();
        LoadOfficialPrefabs(); // load the game TMPro font and official prefabs before creating any text

        // fullscreen background: the panel IS the screen, like the original 个人效果 view.
        // raycastTarget stays true: click does nothing (one-shot decision, accidental dismiss undesired)
        var bg = NewRect("BG", canvasGo.transform);
        bg.gameObject.AddComponent<Image>().color = new Color(0.051f, 0.059f, 0.070f, 1f);
        Stretch(bg);

        // fullscreen content root (no window frame)
        var window = NewRect("Content", bg);
        Stretch(window);

        // title bar
        var title = NewTmp("Title", window, "个人效果", 36, Gold, true);
        if (title != null)
        {
            title.rectTransform.anchorMin = new Vector2(0f, 1f);
            title.rectTransform.anchorMax = new Vector2(1f, 1f);
            title.rectTransform.anchoredPosition = new Vector2(0, -36);
            title.rectTransform.sizeDelta = new Vector2(0, 56);
            title.alignment = TMPro.TextAlignmentOptions.Center;
        }
        else
        {
            var titleLegacy = NewText("Title", window, _font, "个人效果", 36, FontStyle.Bold);
            titleLegacy.color = Gold;
            titleLegacy.rectTransform.anchorMin = new Vector2(0f, 1f);
            titleLegacy.rectTransform.anchorMax = new Vector2(1f, 1f);
            titleLegacy.rectTransform.anchoredPosition = new Vector2(0, -36);
            titleLegacy.rectTransform.sizeDelta = new Vector2(0, 56);
            titleLegacy.alignment = TextAnchor.MiddleCenter;
        }

        var subtitle = NewTmp("Subtitle", window,
            "仅适用于玩家角色的正面和负面特质。每项修正效果都带有对应的点数。总点数必须大于等于 0 才能创建角色。", 18, TextDim);
        if (subtitle != null)
        {
            subtitle.rectTransform.anchorMin = new Vector2(0f, 1f);
            subtitle.rectTransform.anchorMax = new Vector2(1f, 1f);
            subtitle.rectTransform.anchoredPosition = new Vector2(0, -86);
            subtitle.rectTransform.sizeDelta = new Vector2(-60, 30);
            subtitle.alignment = TMPro.TextAlignmentOptions.Center;
        }
        else
        {
            var subtitleLegacy = NewText("Subtitle", window, _font,
                "仅适用于玩家角色的正面和负面特质。每项修正效果都带有对应的点数。总点数必须大于等于 0 才能创建角色。", 18);
            subtitleLegacy.color = TextDim;
            subtitleLegacy.rectTransform.anchorMin = new Vector2(0f, 1f);
            subtitleLegacy.rectTransform.anchorMax = new Vector2(1f, 1f);
            subtitleLegacy.rectTransform.anchoredPosition = new Vector2(0, -86);
            subtitleLegacy.rectTransform.sizeDelta = new Vector2(-60, 30);
            subtitleLegacy.alignment = TextAnchor.MiddleCenter;
        }

        var divider = NewRect("Divider", window);
        divider.gameObject.AddComponent<Image>().color = new Color(Gold.r, Gold.g, Gold.b, 0.45f);
        divider.anchorMin = new Vector2(0f, 1f);
        divider.anchorMax = new Vector2(1f, 1f);
        divider.anchoredPosition = new Vector2(0, -108);
        divider.sizeDelta = new Vector2(-80, 1.5f);

        // two columns: left = positive traits, right = negative traits (original 个人效果 layout)
        var positives = _traits.Where(t => t.Value<string>("kind") == "positive").ToList();
        var negatives = _traits.Where(t => t.Value<string>("kind") == "negative").ToList();
        CreateColumn(window, new Vector2(0.02f, 0.115f), new Vector2(0.33f, 0.865f),
            "正面效果", positives.Count, TextPos, positives);
        BuildCenterColumn(window);
        CreateColumn(window, new Vector2(0.67f, 0.115f), new Vector2(0.98f, 0.865f),
            "负面效果", negatives.Count, TextNeg, negatives);

        // footer: error line + original-style buttons (重置 / 下一步 / 返回)
        _errorText = NewTmp("Error", window, "", 20, Red);
        if (_errorText != null)
        {
            _errorText.rectTransform.anchorMin = new Vector2(0.03f, 0.068f);
            _errorText.rectTransform.anchorMax = new Vector2(0.97f, 0.106f);
            _errorText.rectTransform.offsetMin = Vector2.zero;
            _errorText.rectTransform.offsetMax = Vector2.zero;
            _errorText.alignment = TMPro.TextAlignmentOptions.Left;
        }

        if (!TryNewOfficialButton(window, "重置", new Vector2(0.03f, 0.012f), new Vector2(0.14f, 0.061f), ResetSelection))
        {
            NewButton(window, _font, "重置", new Vector2(0.03f, 0.012f), new Vector2(0.14f, 0.061f),
                RowBg, TextMain, ResetSelection);
        }
        if (!TryNewOfficialButton(window, "下一步", new Vector2(0.40f, 0.012f), new Vector2(0.53f, 0.061f), OnConfirm))
        {
            NewButton(window, _font, "下一步", new Vector2(0.40f, 0.012f), new Vector2(0.53f, 0.061f),
                Gold, new Color(0.10f, 0.11f, 0.13f), OnConfirm);
        }
        if (!TryNewOfficialButton(window, "返回", new Vector2(0.55f, 0.012f), new Vector2(0.68f, 0.061f), Close))
        {
            NewButton(window, _font, "返回", new Vector2(0.55f, 0.012f), new Vector2(0.68f, 0.061f),
                RowBg, TextMain, Close);
        }

        // confirm dialog opens lazily: official MessageWindow first, self-drawn only as fallback
        Refresh();
    }

    /// <summary>Original-style modal: 「保存修正效果」 — accept/cancel centered over the panel.</summary>
    private void BuildConfirmDialog(Transform root)
    {
        var backdrop = NewRect("ConfirmBackdrop", root);
        Stretch(backdrop);
        // raycastTarget stays true: blocks clicks on the panel below while the dialog is open
        backdrop.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.7f);

        var dlg = NewRect("ConfirmDialog", backdrop);
        dlg.anchorMin = dlg.anchorMax = new Vector2(0.5f, 0.5f);
        dlg.sizeDelta = new Vector2(560, 320);
        var dlgImg = dlg.gameObject.AddComponent<Image>();
        dlgImg.color = PanelBg;
        var dlgOutline = dlg.gameObject.AddComponent<Outline>();
        dlgOutline.effectColor = new Color(Gold.r, Gold.g, Gold.b, 0.6f);
        dlgOutline.effectDistance = new Vector2(1.5f, -1.5f);
        TryOfficialFrame(dlg);

        var dlgTitle = NewTmp("DlgTitle", dlg, "保存修正效果", 26, Gold, true);
        var dlgMsg = NewTmp("DlgMsg", dlg, "确定要保存已选定的修正效果吗？\n该操作无法撤销", 20, TextMain);
        if (dlgTitle == null || dlgMsg == null)
        {
            // no TMP font in the menu: the dialog would be unreadable; skip instead of NRE (audit ML-2)
            SeasonTraitsPlugin.Log.LogWarning("SeasonTraits: TMP font unavailable, confirmation dialog skipped");
            return;
        }
        dlgTitle.rectTransform.anchorMin = new Vector2(0f, 1f);
        dlgTitle.rectTransform.anchorMax = new Vector2(1f, 1f);
        dlgTitle.rectTransform.anchoredPosition = new Vector2(0, -28);
        dlgTitle.rectTransform.sizeDelta = new Vector2(0, 56);
        dlgTitle.alignment = TMPro.TextAlignmentOptions.Center;

        dlgMsg.rectTransform.anchorMin = new Vector2(0f, 0.30f);
        dlgMsg.rectTransform.anchorMax = new Vector2(1f, 0.74f);
        dlgMsg.rectTransform.offsetMin = Vector2.zero;
        dlgMsg.rectTransform.offsetMax = Vector2.zero;
        dlgMsg.alignment = TMPro.TextAlignmentOptions.Center;

        // both buttons use the same construction and mirrored anchors: the official prefab's texture
        // sizing is unpredictable inside the modal and rendered 接受/取消 misaligned
        NewButton(dlg, _font, "接受", new Vector2(0.14f, 0.06f), new Vector2(0.46f, 0.20f),
            Gold, new Color(0.10f, 0.11f, 0.13f), ApplySelection);
        NewButton(dlg, _font, "取消", new Vector2(0.54f, 0.06f), new Vector2(0.86f, 0.20f),
            RowBg, TextMain, HideConfirm);

        _confirmDialog = backdrop.gameObject;
        _confirmDialog.SetActive(false);
    }

    private void HideConfirm()
    {
        if (_panelHiddenForConfirm)
        {
            _panelHiddenForConfirm = false;
            _canvasRoot.gameObject.SetActive(true);
        }
        _confirmDialog?.SetActive(false);
    }

    /// <summary>One full-width trait column: fixed "label (count)" band on top, independently scrolling rows below.</summary>
    private void CreateColumn(RectTransform parent, Vector2 anchorMin, Vector2 anchorMax,
        string label, int count, Color accent, List<JToken> traits)
    {
        var column = NewRect("Column_" + label, parent);
        column.anchorMin = anchorMin;
        column.anchorMax = anchorMax;
        column.offsetMin = Vector2.zero;
        column.offsetMax = Vector2.zero;
        // raycastable background: wheel events over gaps/below the list still land on this column
        column.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.25f);

        // fixed section header (not part of the scrolling content, like the original)
        var header = NewRect("Header", column);
        header.anchorMin = new Vector2(0f, 1f);
        header.anchorMax = new Vector2(1f, 1f);
        header.anchoredPosition = new Vector2(0, -22);
        header.sizeDelta = new Vector2(0, 44);
        var headerImg = header.gameObject.AddComponent<Image>();
        headerImg.color = new Color(accent.r, accent.g, accent.b, 0.16f);
        headerImg.raycastTarget = false;
        TryOfficialFrame(header); // official border sprite on the header box
        var headerText = NewTmp("Label", header, $"{label} ({count})", 22, accent, true);
        if (headerText != null)
        {
            headerText.rectTransform.anchorMin = Vector2.zero;
            headerText.rectTransform.anchorMax = Vector2.one;
            headerText.rectTransform.offsetMin = new Vector2(16, 0);
            headerText.rectTransform.offsetMax = Vector2.zero;
            headerText.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
        }

        var viewport = NewRect("Viewport", column);
        viewport.gameObject.AddComponent<RectMask2D>();
        viewport.anchorMin = Vector2.zero;
        viewport.anchorMax = Vector2.one;
        viewport.offsetMin = Vector2.zero;
        viewport.offsetMax = new Vector2(0, -46); // below the fixed header

        var content = NewRect("Content", viewport);
        content.anchorMin = new Vector2(0, 1);
        content.anchorMax = new Vector2(1, 1);
        content.pivot = new Vector2(0.5f, 1f);
        content.offsetMin = Vector2.zero; // default 100x100 sizeDelta would shift rows 50px left of the mask and clip row starts
        content.offsetMax = Vector2.zero;
        var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        var layout = content.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.childForceExpandHeight = false;
        layout.childForceExpandWidth = true;
        layout.spacing = 0; // seamless: options touch each other, no dead gaps between rows
        layout.padding = new RectOffset(8, 8, 4, 4);
        var wheel = column.gameObject.AddComponent<WheelScroll>();
        wheel.Content = content;
        wheel.AfterMove = RepaintRows;
        _scrollContents.Add(content);

        foreach (var trait in traits)
        {
            AddRow(content, trait);
        }
    }

    /// <summary>Middle column: scrollable list of selected traits + fixed 剩余点数 band at the bottom.</summary>
    private void BuildCenterColumn(RectTransform parent)
    {
        var column = NewRect("Column_Selected", parent);
        column.anchorMin = new Vector2(0.36f, 0.115f);
        column.anchorMax = new Vector2(0.64f, 0.865f);
        column.offsetMin = Vector2.zero;
        column.offsetMax = Vector2.zero;
        column.gameObject.AddComponent<Image>().color = new Color(0f, 0f, 0f, 0.25f);

        var header = NewRect("Header", column);
        header.anchorMin = new Vector2(0f, 1f);
        header.anchorMax = new Vector2(1f, 1f);
        header.anchoredPosition = new Vector2(0, -22);
        header.sizeDelta = new Vector2(0, 44);
        var headerImg = header.gameObject.AddComponent<Image>();
        headerImg.color = new Color(Gold.r, Gold.g, Gold.b, 0.16f);
        headerImg.raycastTarget = false;
        TryOfficialFrame(header); // official border sprite on the header box
        _centerHeaderText = NewTmp("Label", header, "已选特质 (0)", 22, Gold, true);
        if (_centerHeaderText != null)
        {
            _centerHeaderText.rectTransform.anchorMin = Vector2.zero;
            _centerHeaderText.rectTransform.anchorMax = Vector2.one;
            _centerHeaderText.rectTransform.offsetMin = new Vector2(16, 0);
            _centerHeaderText.rectTransform.offsetMax = Vector2.zero;
            _centerHeaderText.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
        }

        // remaining points, fixed at the bottom of the column
        var band = NewRect("BalanceBand", column);
        band.anchorMin = new Vector2(0f, 0f);
        band.anchorMax = new Vector2(1f, 0f);
        band.anchoredPosition = new Vector2(0, 28);
        band.sizeDelta = new Vector2(0, 56);
        var bandImg = band.gameObject.AddComponent<Image>();
        bandImg.color = new Color(Gold.r, Gold.g, Gold.b, 0.10f);
        bandImg.raycastTarget = false;
        TryOfficialFrame(band); // same official border as the column headers
        _balanceText = NewTmp("Balance", band, "剩余点数: 0", 26, Green, true);
        if (_balanceText != null)
        {
            Stretch(_balanceText.rectTransform);
            _balanceText.alignment = TMPro.TextAlignmentOptions.Center;
        }

        var viewport = NewRect("Viewport", column);
        viewport.gameObject.AddComponent<RectMask2D>();
        viewport.anchorMin = Vector2.zero;
        viewport.anchorMax = Vector2.one;
        viewport.offsetMin = new Vector2(0, 58); // above the balance band
        viewport.offsetMax = new Vector2(0, -46); // below the header

        var content = NewRect("Content", viewport);
        content.anchorMin = new Vector2(0, 1);
        content.anchorMax = new Vector2(1, 1);
        content.pivot = new Vector2(0.5f, 1f);
        content.offsetMin = Vector2.zero; // same drift fix: entries start at the mask edge
        content.offsetMax = Vector2.zero;
        var fitter = content.gameObject.AddComponent<ContentSizeFitter>();
        fitter.verticalFit = ContentSizeFitter.FitMode.PreferredSize;
        var layout = content.gameObject.AddComponent<VerticalLayoutGroup>();
        layout.childForceExpandHeight = false;
        layout.childForceExpandWidth = true;
        layout.spacing = 0;
        layout.padding = new RectOffset(8, 8, 4, 4);
        var wheel = column.gameObject.AddComponent<WheelScroll>();
        wheel.Content = content;
        wheel.AfterMove = RepaintRows;
        _centerContent = content;
        _scrollContents.Add(content);

        RebuildCenter();
    }

    /// <summary>One entry in the middle column: trait name (kind-colored) + points on the right.
    /// Anchored children only (no layout groups): uGUI's HorizontalLayoutGroup + CJK preferred-width
    /// math clipped the names at the mask edge.</summary>
    private void AddCenterEntry(RectTransform content, JToken trait)
    {
        var isNegative = trait.Value<string>("kind") == "negative";
        var points = trait.Value<int?>("points") ?? 0;
        var accent = isNegative ? TextNeg : TextPos;

        var row = NewRect("Sel_" + trait.Value<string>("id"), content);
        row.gameObject.AddComponent<Image>().color = RowBg;
        var le = row.gameObject.AddComponent<LayoutElement>();
        le.minHeight = 40;

        var name = NewTmp("Name", row, trait.Value<string>("nameZh"), 18, accent, true);
        if (name != null)
        {
            name.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
            name.enableWordWrapping = false; // short names must never clip
            name.rectTransform.anchorMin = new Vector2(0f, 0f);
            name.rectTransform.anchorMax = new Vector2(1f, 1f);
            name.rectTransform.offsetMin = new Vector2(12, 0);
            name.rectTransform.offsetMax = new Vector2(-52, 0);
        }

        var pts = NewTmp("Points", row, (isNegative ? "+" : "-") + points, 18, isNegative ? Green : Gold, true);
        if (pts != null)
        {
            pts.alignment = TMPro.TextAlignmentOptions.MidlineRight;
            pts.rectTransform.anchorMin = new Vector2(1f, 0f);
            pts.rectTransform.anchorMax = new Vector2(1f, 1f);
            pts.rectTransform.offsetMin = new Vector2(-52, 0);
            pts.rectTransform.offsetMax = new Vector2(-12, 0);
        }
    }

    /// <summary>Rebuild the middle column list from the current selection.</summary>
    private void RebuildCenter()
    {
        for (var i = _centerContent.childCount - 1; i >= 0; i--)
        {
            var go = _centerContent.GetChild(i).gameObject;
            go.SetActive(false);
            Destroy(go);
        }
        var selected = _rows.Values.Where(r => r.Selected).Select(r => r.Trait).ToList();
        if (_centerHeaderText != null)
        {
            _centerHeaderText.text = $"已选特质 ({selected.Count})";
        }
        if (selected.Count == 0)
        {
            var empty = NewTmp("Empty", _centerContent, "未选择特质", 18, TextDim);
            if (empty != null)
            {
                var le = empty.rectTransform.gameObject.AddComponent<LayoutElement>();
                le.minHeight = 40;
                empty.alignment = TMPro.TextAlignmentOptions.Center;
            }
            return;
        }
        foreach (var trait in selected)
        {
            if (!TryOfficialCenterEntry(_centerContent, trait))
            {
                AddCenterEntry(_centerContent, trait);
            }
        }
    }

    /// <summary>Build one trait row from the official DefaultUIButton prefab (frame + hover states + icon slot).
    /// Returns false when the prefab is unavailable so callers fall back to the self-drawn row.</summary>
    private bool TryOfficialRow(RectTransform content, JToken trait)
    {
        if (_buttonPrefab == null)
        {
            return false;
        }
        try
        {
            var id = trait.Value<string>("id");
            var isNegative = trait.Value<string>("kind") == "negative";
            var points = trait.Value<int?>("points") ?? 0;
            var accent = isNegative ? TextNeg : TextPos;

            var clone = Instantiate(_buttonPrefab);
            clone.name = "Row_" + id;
            clone.transform.SetParent(content, false);
            var rt = (RectTransform)clone.transform;
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            // drop any content fitter the prefab carries: the row must be column-driven, not content-driven
            var fitter = clone.GetComponent<ContentSizeFitter>();
            if (fitter != null)
            {
                Destroy(fitter);
            }
            // the prefab may carry its own LayoutElement: reuse it, a second one would be ignored by the layout group
            var le = clone.GetComponent<LayoutElement>() ?? clone.AddComponent<LayoutElement>();
            le.minHeight = 58;
            le.preferredHeight = -1;
            le.flexibleHeight = 0;
            le.preferredWidth = -1;
            le.flexibleWidth = 1; // the column's VerticalLayoutGroup stretches the row to full column width

            var dui = clone.GetComponent<EFT.UI.DefaultUIButton>();
            if (dui == null)
            {
                Destroy(clone);
                return false;
            }
            // the prefab's own HorizontalLayoutGroup forces its children (and our overlay) into a single horizontal line
            var hlg = clone.GetComponent<HorizontalLayoutGroup>();
            if (hlg != null)
            {
                Destroy(hlg);
            }
            // disable all prefab children: the hover highlight and icon/label are children, not the root background
            foreach (Transform child in clone.transform)
            {
                child.gameObject.SetActive(false);
            }
            // guarantee the root has a raycast target so the row is clickable
            var rootImg = clone.GetComponent<Image>();
            if (rootImg == null)
            {
                rootImg = clone.gameObject.AddComponent<Image>();
            }
            rootImg.sprite = null; // remove the PurchaseButton texture; rows are just text over the dark panel
            rootImg.color = new Color(0f, 0f, 0f, 0f); // invisible click catcher
            rootImg.raycastTarget = true;
            // disable the prefab's built-in button behaviour entirely (hover/click/sound) and handle clicks ourselves
            dui.enabled = false;
            var btn = clone.GetComponent<Button>();
            if (btn != null)
            {
                btn.enabled = false;
            }
            dui.SetHeaderText(""); // frame/states from the prefab; our content is drawn on an overlay on top

            // overlay: our content lives under one plain container, anchored freely across the row
            var layer = NewRect("Overlay", clone.transform);
            Stretch(layer);

            // trait icon: own Image on the overlay, left side vertically centered
            var icon = NewRect("Icon", layer);
            icon.anchorMin = new Vector2(0f, 0.5f);
            icon.anchorMax = new Vector2(0f, 0.5f);
            icon.anchoredPosition = new Vector2(27, 0);
            icon.sizeDelta = new Vector2(34, 34);
            var iconImg = icon.gameObject.AddComponent<Image>();
            iconImg.sprite = LoadTraitIcon(trait.Value<string>("icon"));
            iconImg.preserveAspect = true;
            iconImg.raycastTarget = false;

            // line 1: name (kind accent) + points · line 2: description — game-font TMPro when available
            var rowState = new RowState
            {
                Id = id,
                Trait = trait,
                RowRoot = rt,
            };
            rowState.NameTmp = LayoutRowText(layer, "Name", trait.Value<string>("nameZh"), 18f, accent, true,
                new Vector2(48, 32), new Vector2(-116, -2), TMPro.TextAlignmentOptions.MidlineLeft);
            if (rowState.NameTmp == null)
            {
                rowState.NameText = AddRowSurrogate(layer, "Name", trait.Value<string>("nameZh"), 18, accent, new Vector2(48, 32), new Vector2(-116, -2));
            }
            rowState.PointsTmp = LayoutRowText(layer, "Points", (isNegative ? "+" : "-") + points + " 点", 18f, isNegative ? Green : Gold, true,
                new Vector2(-108, 32), new Vector2(-64, -2), TMPro.TextAlignmentOptions.MidlineRight);
            if (rowState.PointsTmp == null)
            {
                rowState.PointsText = AddRowSurrogate(layer, "Points", (isNegative ? "+" : "-") + points + " 点", 18, isNegative ? Green : Gold, new Vector2(-108, 32), new Vector2(-64, -2));
                rowState.PointsText.alignment = TextAnchor.MiddleRight;
            }
            rowState.DescTmp = LayoutRowText(layer, "Desc", trait.Value<string>("descriptionZh") ?? "", 15f, TextDim, false,
                new Vector2(48, 2), new Vector2(-64, -30), TMPro.TextAlignmentOptions.MidlineLeft);
            if (rowState.DescTmp != null)
            {
                rowState.DescTmp.overflowMode = TMPro.TextOverflowModes.Ellipsis; // wrap first, ellipsize instead of overflowing the zone
            }
            else
            {
                rowState.DescText = AddRowSurrogate(layer, "Desc", trait.Value<string>("descriptionZh") ?? "", 15, TextDim, new Vector2(48, 2), new Vector2(-64, -30));
            }

            // checkbox: official toggle when available, small fallback mark otherwise (centered right)
            var check = NewRect("Check", layer);
            check.anchorMin = new Vector2(1f, 0.5f);
            check.anchorMax = new Vector2(1f, 0.5f);
            check.anchoredPosition = new Vector2(-45, 0);
            check.sizeDelta = new Vector2(26, 24);
            rowState.OfficialToggle = TryOfficialToggle(check);
            rowState.CheckMark = MakeCheckMark(check, accent);
            if (rowState.OfficialToggle != null)
            {
                rowState.CheckMark.gameObject.SetActive(false);
            }

            // disable the prefab's built-in button behaviour entirely (hover/click/sound) and handle clicks ourselves
            dui.enabled = false;
            if (btn != null)
            {
                btn.enabled = false;
            }
            var clicker = clone.gameObject.AddComponent<RowClickHandler>();
            clicker.OnClick = () => ToggleRow(rowState);

            _rows[id] = rowState;
            return true;
        }
        catch (Exception e)
        {
            SeasonTraitsPlugin.Log.LogWarning($"SeasonTraits: official row '{trait.Value<string>("id")}' failed ({e.Message}), using fallback");
            return false;
        }
    }

    /// <summary>Anchored TMPro row text (game font); null when no TMPro font asset was found.</summary>
    private TMPro.TextMeshProUGUI LayoutRowText(Transform parent, string name, string content, float size, Color color, bool bold, Vector2 offsetMin, Vector2 offsetMax, TMPro.TextAlignmentOptions align)
    {
        var tmp = NewTmp(name, parent, content, size, color, bold);
        if (tmp == null)
        {
            return null;
        }
        tmp.rectTransform.anchorMin = Vector2.zero;
        tmp.rectTransform.anchorMax = Vector2.one;
        tmp.rectTransform.offsetMin = offsetMin;
        tmp.rectTransform.offsetMax = offsetMax;
        tmp.alignment = align;
        return tmp;
    }

    /// <summary>Middle-column summary entry from the official DefaultUIButton prefab (frame + game-font name + points).</summary>
    private bool TryOfficialCenterEntry(RectTransform content, JToken trait)
    {
        if (_buttonPrefab == null)
        {
            return false;
        }
        try
        {
            var id = trait.Value<string>("id");
            var isNegative = trait.Value<string>("kind") == "negative";
            var points = trait.Value<int?>("points") ?? 0;
            var accent = isNegative ? TextNeg : TextPos;

            var clone = Instantiate(_buttonPrefab);
            clone.name = "Sel_" + id;
            clone.transform.SetParent(content, false);
            var rt = (RectTransform)clone.transform;
            rt.anchorMin = new Vector2(0f, 1f);
            rt.anchorMax = new Vector2(1f, 1f);
            rt.pivot = new Vector2(0.5f, 1f);
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            // drop any content fitter the prefab carries: the entry must be column-driven, not content-driven
            var fitter = clone.GetComponent<ContentSizeFitter>();
            if (fitter != null)
            {
                Destroy(fitter);
            }
            var le = clone.GetComponent<LayoutElement>() ?? clone.AddComponent<LayoutElement>();
            le.minHeight = 42;
            le.preferredHeight = -1;
            le.flexibleHeight = 0;
            le.preferredWidth = -1;
            le.flexibleWidth = 1; // the column's VerticalLayoutGroup stretches the entry to full column width

            var dui = clone.GetComponent<EFT.UI.DefaultUIButton>();
            if (dui == null)
            {
                Destroy(clone);
                return false;
            }
            // the prefab's own HorizontalLayoutGroup forces its children (and our overlay) into a single horizontal line
            var hlg = clone.GetComponent<HorizontalLayoutGroup>();
            if (hlg != null)
            {
                Destroy(hlg);
            }
            // disable all prefab children: the hover highlight and icon/label are children, not the root background
            foreach (Transform child in clone.transform)
            {
                child.gameObject.SetActive(false);
            }
            // guarantee the root has a raycast target
            var rootImg = clone.GetComponent<Image>();
            if (rootImg == null)
            {
                rootImg = clone.gameObject.AddComponent<Image>();
            }
            rootImg.sprite = null; // remove the PurchaseButton texture
            rootImg.color = new Color(0f, 0f, 0f, 0f); // invisible click catcher
            rootImg.raycastTarget = true;
            // disable the prefab's built-in button behaviour entirely for the center entry too
            dui.enabled = false;
            var btn = clone.GetComponent<Button>();
            if (btn != null)
            {
                btn.enabled = false;
            }
            dui.SetHeaderText(""); // plain frame; the summary lines are ours

            // disable the prefab's built-in button behaviour entirely (hover/click/sound) for the center entry too
            dui.enabled = false;
            if (btn != null)
            {
                btn.enabled = false;
            }

            // overlay: content stays outside the prefab's internal layout group, anchored freely across the entry
            var layer = NewRect("Overlay", clone.transform);
            Stretch(layer);

            if (LayoutRowText(layer, "Name", trait.Value<string>("nameZh"), 18f, accent, true,
                    new Vector2(12, 6), new Vector2(-96, -6), TMPro.TextAlignmentOptions.MidlineLeft) == null)
            {
                AddRowSurrogate(layer, "Name", trait.Value<string>("nameZh"), 18, accent, new Vector2(12, 6), new Vector2(-96, -6));
            }
            if (LayoutRowText(layer, "Points", (isNegative ? "+" : "-") + points, 18f, isNegative ? Green : Gold, true,
                    new Vector2(-92, 6), new Vector2(-52, -6), TMPro.TextAlignmentOptions.MidlineRight) == null)
            {
                var pts = AddRowSurrogate(layer, "Points", (isNegative ? "+" : "-") + points, 18, isNegative ? Green : Gold, new Vector2(-92, 6), new Vector2(-52, -6));
                pts.alignment = TextAnchor.MiddleRight;
            }
            return true;
        }
        catch (Exception e)
        {
            SeasonTraitsPlugin.Log.LogWarning($"SeasonTraits: official center entry '{trait.Value<string>("id")}' failed ({e.Message}), using fallback");
            return false;
        }
    }

    /// <summary>Text layered on an official prefab clone: anchored zones, no layout groups.</summary>
    private Text AddRowSurrogate(Transform parent, string name, string content, int size, Color color, Vector2 offsetMin, Vector2 offsetMax)
    {
        var text = NewText(name, parent, _font, content, size);
        text.color = color;
        text.rectTransform.anchorMin = Vector2.zero;
        text.rectTransform.anchorMax = Vector2.one;
        text.rectTransform.offsetMin = offsetMin;
        text.rectTransform.offsetMax = offsetMax;
        text.alignment = TextAnchor.MiddleLeft;
        return text;
    }

    /// <summary>Checkmark marker; hidden when the official toggle exists.</summary>
    private static Image MakeCheckMark(RectTransform parent, Color color)
    {
        var mark = NewRect("Mark", parent);
        Stretch(mark);
        mark.offsetMin = new Vector2(4, 4);
        mark.offsetMax = new Vector2(-4, -4);
        var img = mark.gameObject.AddComponent<Image>();
        img.sprite = GetCheckmarkSprite();
        img.color = color;
        img.enabled = false;
        return img;
    }

    private static Sprite _checkmarkSprite;
    private static Sprite GetCheckmarkSprite()
    {
        if (_checkmarkSprite != null)
        {
            return _checkmarkSprite;
        }
        const int size = 32;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var clear = new Color(0f, 0f, 0f, 0f);
        var pixels = new Color[size * size];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = clear;
        }
        DrawThickLine(pixels, size, 6, 16, 13, 7, 2.5f, Color.white);
        DrawThickLine(pixels, size, 13, 7, 27, 25, 2.5f, Color.white);
        tex.SetPixels(pixels);
        tex.Apply();
        _checkmarkSprite = Sprite.Create(tex, new Rect(0f, 0f, size, size), new Vector2(0.5f, 0.5f), 100f, 0u, SpriteMeshType.FullRect);
        _checkmarkSprite.name = "SeasonTraitsCheckmark";
        return _checkmarkSprite;
    }

    private static void DrawThickLine(Color[] pixels, int size, int x0, int y0, int x1, int y1, float thickness, Color color)
    {
        int minX = Mathf.Clamp(Mathf.Min(x0, x1) - Mathf.CeilToInt(thickness) - 1, 0, size - 1);
        int maxX = Mathf.Clamp(Mathf.Max(x0, x1) + Mathf.CeilToInt(thickness) + 1, 0, size - 1);
        int minY = Mathf.Clamp(Mathf.Min(y0, y1) - Mathf.CeilToInt(thickness) - 1, 0, size - 1);
        int maxY = Mathf.Clamp(Mathf.Max(y0, y1) + Mathf.CeilToInt(thickness) + 1, 0, size - 1);
        float thick2 = thickness * thickness;
        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                if (SqrDistanceToSegment(x, y, x0, y0, x1, y1) <= thick2)
                {
                    pixels[y * size + x] = color;
                }
            }
        }
    }

    private static float SqrDistanceToSegment(int px, int py, int x0, int y0, int x1, int y1)
    {
        float dx = x1 - x0;
        float dy = y1 - y0;
        float len2 = dx * dx + dy * dy;
        float t = len2 == 0f ? 0f : Mathf.Clamp01(((px - x0) * dx + (py - y0) * dy) / len2);
        float projX = x0 + t * dx;
        float projY = y0 + t * dy;
        float dxp = px - projX;
        float dyp = py - projY;
        return dxp * dxp + dyp * dyp;
    }

    private void AddRow(RectTransform content, JToken trait)
    {
        if (TryOfficialRow(content, trait))
        {
            return;
        }
        var id = trait.Value<string>("id");
        var kind = trait.Value<string>("kind");
        var points = trait.Value<int?>("points") ?? 0;
        var isNegative = kind == "negative";
        var accent = isNegative ? TextNeg : TextPos;

        var row = NewRect($"Row_{id}", content);
        var rowBg = row.gameObject.AddComponent<Image>();
        rowBg.color = RowBg;
        var rowLayout = row.gameObject.AddComponent<LayoutElement>();
        rowLayout.minHeight = 50;

        var h = row.gameObject.AddComponent<HorizontalLayoutGroup>();
        h.childForceExpandWidth = false;
        h.childForceExpandHeight = true;
        h.childControlWidth = true;
        h.childControlHeight = true;
        h.padding = new RectOffset(10, 10, 6, 6);
        h.spacing = 10;

        var rowButton = row.gameObject.AddComponent<Button>();
        rowButton.targetGraphic = rowBg;
        rowButton.transition = Selectable.Transition.None; // hover/selected drawn manually

        // left accent bar (visible when selected)
        var bar = NewRect("Bar", row);
        bar.gameObject.AddComponent<Image>().color = accent;
        var barLe = bar.gameObject.AddComponent<LayoutElement>();
        barLe.minWidth = 4;
        barLe.flexibleWidth = 0;
        var barImg = bar.GetComponent<Image>();
        barImg.color = new Color(accent.r, accent.g, accent.b, 0f); // transparent until selected

        // trait icon (embedded resource keyed by the server-sent hex id)
        var icon = NewRect("Icon", row);
        var iconLe = icon.gameObject.AddComponent<LayoutElement>();
        iconLe.minWidth = 32;
        iconLe.flexibleWidth = 0;
        var iconImg = icon.gameObject.AddComponent<Image>();
        iconImg.sprite = LoadTraitIcon(trait.Value<string>("icon"));
        iconImg.preserveAspect = true;
        iconImg.color = Color.white;
        iconImg.raycastTarget = false;

        // name + description stacked
        var mid = NewRect("Mid", row);
        var midLe = mid.gameObject.AddComponent<LayoutElement>();
        midLe.flexibleWidth = 1000;
        var v = mid.gameObject.AddComponent<VerticalLayoutGroup>();
        v.childForceExpandHeight = false;
        v.childForceExpandWidth = true;
        v.spacing = 2;

        var nameText = NewTmp("Name", mid, trait.Value<string>("nameZh"), 20, accent, true);
        if (nameText != null)
        {
            var nameLe = nameText.rectTransform.gameObject.AddComponent<LayoutElement>();
            nameLe.minHeight = 22;
        }
        var descText = NewTmp("Desc", mid, trait.Value<string>("descriptionZh") ?? "", 15, TextDim);
        if (descText != null)
        {
            var descLe = descText.rectTransform.gameObject.AddComponent<LayoutElement>();
            descLe.minHeight = 16;
        }

        // points on the right: negative traits grant, positive traits cost
        var pointsText = NewTmp("Points", row, isNegative ? $"+{points} 点" : $"-{points} 点", 18, isNegative ? Green : Gold, true);
        if (pointsText != null)
        {
            var pointsLe = pointsText.rectTransform.gameObject.AddComponent<LayoutElement>();
            pointsLe.minWidth = 80;
            pointsText.alignment = TMPro.TextAlignmentOptions.MidlineRight;
        }

        // checkbox on the far right: bordered square + inner fill when selected
        var check = NewRect("Check", row);
        var checkLe = check.gameObject.AddComponent<LayoutElement>();
        checkLe.minWidth = 26;
        checkLe.flexibleWidth = 0;
        var checkBg = check.gameObject.AddComponent<Image>();
        checkBg.color = new Color(0f, 0f, 0f, 0f); // invisible container; box drawn as child
        var box = NewRect("Box", check);
        box.anchorMin = box.anchorMax = new Vector2(0.5f, 0.5f);
        box.sizeDelta = new Vector2(22, 22);
        var boxImg = box.gameObject.AddComponent<Image>();
        boxImg.color = new Color(1f, 1f, 1f, 0.18f);
        var boxOutline = box.gameObject.AddComponent<Outline>();
        boxOutline.effectColor = new Color(1f, 1f, 1f, 0.45f);
        boxOutline.effectDistance = new Vector2(1, 1);
        var mark = NewRect("Mark", box);
        mark.anchorMin = Vector2.zero;
        mark.anchorMax = Vector2.one;
        mark.offsetMin = new Vector2(3, 3);
        mark.offsetMax = new Vector2(-3, -3);
        var markImg = mark.gameObject.AddComponent<Image>();
        markImg.color = accent;
        markImg.enabled = false; // shown when selected

        var rowState = new RowState
        {
            Id = id,
            Trait = trait,
            Bg = rowBg,
            Bar = barImg,
            Icon = iconImg,
            CheckMark = markImg,
            RowButton = rowButton,
            NameTmp = nameText,
            PointsTmp = pointsText,
            DescTmp = descText,
            BaseColor = isNegative ? new Color(0.24f, 0.16f, 0.15f) : new Color(0.15f, 0.19f, 0.24f),
        };

        // checkbox visual: official Toggle prefab when available, self-drawn box as fallback
        rowState.OfficialToggle = TryOfficialToggle(check);
        if (rowState.OfficialToggle != null)
        {
            box.gameObject.SetActive(false);
        }

        rowButton.onClick.AddListener(() => ToggleRow(rowState));
        _rows[id] = rowState;
        if (rowState.Bg != null)
        {
            AddHover(rowState); // official rows animate their own hover
        }
    }

    private static void AddHover(RowState s)
    {
        // NOT EventTrigger: it implements IScrollHandler too and swallows wheel events
        // before they reach WheelScroll on the scroll root.
        var hover = s.Bg.gameObject.AddComponent<RowHoverTracker>();
        hover.Row = s;
    }

    private class RowHoverTracker : MonoBehaviour,
        UnityEngine.EventSystems.IPointerEnterHandler, UnityEngine.EventSystems.IPointerExitHandler
    {
        public RowState Row;

        public void OnPointerEnter(UnityEngine.EventSystems.PointerEventData eventData)
        {
            Row.Hovered = true;
            Paint(Row);
        }

        public void OnPointerExit(UnityEngine.EventSystems.PointerEventData eventData)
        {
            Row.Hovered = false;
            Paint(Row);
        }
    }

    private class RowClickHandler : MonoBehaviour, UnityEngine.EventSystems.IPointerClickHandler
    {
        public Action OnClick;

        public void OnPointerClick(UnityEngine.EventSystems.PointerEventData eventData)
        {
            OnClick?.Invoke();
        }
    }

    private void ToggleRow(RowState s)
    {
        if (s.Blocked)
        {
            SetError($"与已选特质互斥: {s.Trait.Value<string>("nameZh")}");
            return;
        }
        if (s.Selected)
        {
            s.Selected = false;
        }
        else
        {
            // deselect conflicting selected rows first: picking one of a group auto-drops the other
            var selected = _rows.Values.Where(r => r.Selected).ToList();
            foreach (var other in selected)
            {
                var aBlocks = (s.Trait.Value<JArray>("blocks") ?? new JArray()).Any(x => x.Value<string>() == other.Id);
                var bBlocks = (other.Trait.Value<JArray>("blocks") ?? new JArray()).Any(x => x.Value<string>() == s.Id);
                if (aBlocks || bBlocks)
                {
                    other.Selected = false;
                }
            }
            s.Selected = true;
        }
        Refresh();
    }

    private void ResetSelection()
    {
        foreach (var row in _rows.Values)
        {
            row.Selected = false;
        }
        Refresh();
    }

    private void OnConfirm()
    {
        var selectedIds = _rows.Values.Where(r => r.Selected).Select(r => r.Id).ToList();
        var selected = _rows.Values.Where(r => r.Selected).Select(r => r.Trait).ToList();
        var balance = selected.Sum(t => t.Value<string>("kind") == "negative" ? t.Value<int?>("points") ?? 0 : -(t.Value<int?>("points") ?? 0));
        if (selectedIds.Count == 0)
        {
            SetError("请先勾选至少一项特质");
            return;
        }
        if (balance < 0)
        {
            SetError($"点数不足: 余额 {balance}，需要 ≥ 0");
            return;
        }
        // valid: open the confirmation — official MessageWindow when available, self-drawn fallback
        OpenConfirm();
    }

    private void OpenConfirm()
    {
        if (TryOfficialConfirm())
        {
            // the native MessageWindow renders on the game's own canvas, which sits under our
            // overlay panel; hide our canvas while it is up (matches the original flow — the
            // confirm pops over the menu). OnClose/HideConfirm brings the panel back.
            _panelHiddenForConfirm = true;
            _canvasRoot.gameObject.SetActive(false);
            return;
        }
        if (_confirmDialog == null)
        {
            BuildConfirmDialog(_canvasRoot);
        }
        _confirmDialog.SetActive(true);
    }

    private bool TryOfficialConfirm()
    {
        try
        {
            var iu = EFT.UI.ItemUiContext.Instance;
            if (iu == null)
            {
                return false;
            }
            var ctx = iu.ShowMessageWindow(
                "确定要保存已选定的修正效果吗？\n该操作无法撤销",
                ApplySelection,
                HideConfirm,
                "保存修正效果",
                0f,
                false,
                TMPro.TextAlignmentOptions.Center);
            if (ctx == null)
            {
                return false;
            }
            _officialConfirmContext = ctx;
            ctx.OnClose += () =>
            {
                if (ReferenceEquals(_officialConfirmContext, ctx))
                {
                    _officialConfirmContext = null;
                    if (!_closed)
                    {
                        _panelHiddenForConfirm = false;
                        _canvasRoot.gameObject.SetActive(true);
                    }
                }
            };
            return true;
        }
        catch (Exception e)
        {
            SeasonTraitsPlugin.Log.LogWarning($"SeasonTraits: official confirm failed, using fallback dialog: {e.Message}");
            return false;
        }
    }

    private void ApplySelection()
    {
        var selectedIds = _rows.Values.Where(r => r.Selected).Select(r => r.Id).ToList();
        string response;
        try
        {
            response = _apply(new JArray(selectedIds));
        }
        catch (Exception e)
        {
            SetError($"请求失败: {e.Message}");
            HideConfirm();
            return;
        }
        try
        {
            var result = JObject.Parse(response);
            if (result.Value<bool?>("success") == true)
            {
                _closed = true;
                Active = null;
                gameObject.SetActive(false);
                _onClosed();
                TraitEffectsState.SetSelected((result["selected"] as JArray)?.Select(t => t.Value<string>()));
                AllergenState.EnsureAllergens(selectedIds.Contains("allergic"));
                LiveProfileMirror.MirrorApplyResult(response);
                SeasonTraitsPlugin.Log.LogInfo($"SeasonTraits: traits applied: {result["selected"]}");
            }
            else
            {
                SetError(result.Value<string>("error") ?? "服务器拒绝了该选择");
                HideConfirm();
                Refresh();
            }
        }
        catch (Exception e)
        {
            SetError($"响应解析失败: {e.Message}");
            HideConfirm();
        }
    }

    private void Close()
    {
        _closed = true;
        Active = null;
        gameObject.SetActive(false);
        _onClosed();
    }

    /// <summary>Mirror of the server rules: balance &gt;= 0 and no mutual exclusions.</summary>
    private void Refresh()
    {
        var selected = _rows.Values.Where(r => r.Selected).ToList();
        var balance = selected.Sum(r => r.Trait.Value<string>("kind") == "negative"
            ? r.Trait.Value<int?>("points") ?? 0
            : -(r.Trait.Value<int?>("points") ?? 0));

        if (_balanceText != null)
        {
            _balanceText.text = $"剩余点数: {balance}";
            _balanceText.color = balance < 0 ? Red : Green;
        }

        RebuildCenter();

        // block conflicting rows with a clearly dimmed visual
        foreach (var row in _rows.Values)
        {
            if (row.Selected)
            {
                continue;
            }
            row.Blocked = selected.Any(s =>
                (s.Trait.Value<JArray>("blocks") ?? new JArray()).Any(b => b.Value<string>() == row.Id)
                || (row.Trait.Value<JArray>("blocks") ?? new JArray()).Any(b => b.Value<string>() == s.Id));
        }

        foreach (var row in _rows.Values)
        {
            Paint(row);
        }

        // conflict hint when clicking was suppressed (blocked row clicked shows message instead)
        if (selected.Count > 0 && balance >= 0)
        {
            SetError("");
        }
        else if (selected.Count == 0)
        {
            SetError("");
        }

        foreach (var c in _scrollContents)
        {
            LayoutRebuilder.ForceRebuildLayoutImmediate(c);
        }
    }

    /// <summary>Error line is optional (no TMP font in the menu): every writer must tolerate null (audit ML-2).</summary>
    private void SetError(string message)
    {
        if (_errorText != null)
        {
            _errorText.text = message;
        }
    }

    private static void SetTextColor(TMPro.TMP_Text tmp, Text legacy, Color color)
    {
        if (tmp != null)
        {
            tmp.color = color;
        }
        if (legacy != null)
        {
            legacy.color = color;
        }
    }

    /// <summary>Draws the row's current state: selected / blocked / hover / normal.</summary>
    private static void Paint(RowState s)
    {
        if (s.Bg == null)
        {
            // official prefab row: the button animates its own states; ours are marker + dimming only
            s.CheckMark.enabled = s.Selected;
            if (s.OfficialToggle != null)
            {
                s.OfficialToggle.gameObject.SetActive(s.Selected);
                s.OfficialToggle.isOn = s.Selected;
            }
            var nameColor = s.Blocked ? TextDim : (s.Trait.Value<string>("kind") == "negative" ? TextNeg : TextPos);
            if (s.NameTmp != null)
            {
                s.NameTmp.color = nameColor;
            }
            else if (s.NameText != null)
            {
                s.NameText.color = nameColor;
            }
            if (s.PointsTmp != null)
            {
                s.PointsTmp.color = s.Blocked ? TextDim : (s.Trait.Value<string>("kind") == "negative" ? Green : Gold);
            }
            else if (s.PointsText != null)
            {
                s.PointsText.color = s.Blocked ? TextDim : (s.Trait.Value<string>("kind") == "negative" ? Green : Gold);
            }
            if (s.DescTmp != null)
            {
                s.DescTmp.color = TextDim;
            }
            else if (s.DescText != null)
            {
                s.DescText.color = TextDim;
            }
            return;
        }
        var hovering = s.Hovered; // maintained by EventTrigger enter/exit, not by polling mouse position
        if (s.Selected)
        {
            s.Bg.color = RowSelected;
            s.Bar.color = new Color(s.Bar.color.r, s.Bar.color.g, s.Bar.color.b, 1f);
            s.CheckMark.enabled = true;
            if (s.OfficialToggle != null)
            {
                s.OfficialToggle.gameObject.SetActive(true);
                s.OfficialToggle.isOn = true;
            }
            s.RowButton.interactable = true;
        }
        else if (s.Blocked)
        {
            s.Bg.color = new Color(s.BaseColor.r * 0.35f, s.BaseColor.g * 0.35f, s.BaseColor.b * 0.35f);
            s.Bar.color = new Color(s.Bar.color.r, s.Bar.color.g, s.Bar.color.b, 0f);
            s.CheckMark.enabled = false;
            if (s.OfficialToggle != null)
            {
                s.OfficialToggle.gameObject.SetActive(false);
                s.OfficialToggle.isOn = false;
            }
            s.RowButton.interactable = true; // stays clickable to explain the block
            SetTextColor(s.NameTmp, s.NameText, TextDim);
            SetTextColor(s.PointsTmp, s.PointsText, TextDim);
            SetTextColor(s.DescTmp, s.DescText, TextDim);
        }
        else
        {
            s.Bg.color = hovering ? RowHover : s.BaseColor;
            s.Bar.color = new Color(s.Bar.color.r, s.Bar.color.g, s.Bar.color.b, 0f);
            s.CheckMark.enabled = false;
            if (s.OfficialToggle != null)
            {
                s.OfficialToggle.gameObject.SetActive(false);
                s.OfficialToggle.isOn = false;
            }
            s.RowButton.interactable = true;
        }
        if (!s.Blocked)
        {
            SetTextColor(s.NameTmp, s.NameText, s.Trait.Value<string>("kind") == "negative" ? TextNeg : TextPos);
            SetTextColor(s.PointsTmp, s.PointsText, s.Trait.Value<string>("kind") == "negative" ? Green : Gold);
            SetTextColor(s.DescTmp, s.DescText, TextDim);
        }
    }

    /// <summary>Icon sprite for the character-screen traits tab (same embedded resources).</summary>
    internal static Sprite GetTraitIcon(string iconId) => LoadTraitIcon(iconId);

    /// <summary>TMP font used by the game UI; safe to call before the selection panel is built.</summary>
    internal static TMPro.TMP_FontAsset ResolveTmpFont()
    {
        if (_tmpFont != null)
        {
            return _tmpFont;
        }

        try
        {
            _tmpFont = Resources.FindObjectsOfTypeAll<TMPro.TMP_Text>()
                .FirstOrDefault(t => t != null && t.font != null)?.font;
            if (_tmpFont == null)
            {
                _tmpFont = TMPro.TMP_Settings.defaultFontAsset;
            }
        }
        catch (Exception e)
        {
            SeasonTraitsPlugin.Log.LogWarning($"SeasonTraits: TMP font probe failed: {e.Message}");
        }

        return _tmpFont;
    }

    /// <summary>Load an embedded trait icon sprite by its hex id; caches per id. Null on missing/broken resource.</summary>
    private static Sprite LoadTraitIcon(string iconId)
    {
        if (string.IsNullOrEmpty(iconId))
        {
            return null;
        }
        if (_iconCache.TryGetValue(iconId, out var cached))
        {
            return cached;
        }
        try
        {
            var asm = typeof(TraitsPanelUI).Assembly;
            var resName = $"SeasonTraits.Client.Icons.{iconId}.png";
            using var stream = asm.GetManifestResourceStream(resName);
            if (stream == null)
            {
                SeasonTraitsPlugin.Log.LogWarning($"SeasonTraits: missing icon resource {resName}");
                return null;
            }
            using var ms = new System.IO.MemoryStream();
            stream.CopyTo(ms);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            tex.LoadImage(ms.ToArray());
            tex.filterMode = FilterMode.Bilinear;
            var sprite = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), new Vector2(0.5f, 0.5f), 100f);
            _iconCache[iconId] = sprite;
            return sprite;
        }
        catch (Exception e)
        {
            SeasonTraitsPlugin.Log.LogWarning($"SeasonTraits: icon '{iconId}' load failed: {e.Message}");
            return null;
        }
    }

    /// <summary>Prefer an official game font loaded at runtime; fall back to the Unity builtin.</summary>
    private static Font FindGameFont()
    {
        try
        {
            var fonts = Resources.FindObjectsOfTypeAll<Font>();
            var official = fonts.FirstOrDefault(f => f != null
                && f.name != "LegacyRuntime.ttf" && f.name != "Arial"
                && (f.name.ToLower().Contains("tahoma") || f.name.ToLower().Contains("roboto") || f.name.ToLower().Contains("bender")));
            if (official != null)
            {
                SeasonTraitsPlugin.Log.LogInfo($"SeasonTraits: using game font '{official.name}'");
                return official;
            }
            var first = fonts.FirstOrDefault(f => f != null && f.name != "LegacyRuntime.ttf" && f.name != "Arial");
            if (first != null)
            {
                SeasonTraitsPlugin.Log.LogInfo($"SeasonTraits: using loaded font '{first.name}'");
                return first;
            }
            SeasonTraitsPlugin.Log.LogInfo("SeasonTraits: no loaded fonts found, using LegacyRuntime.ttf");
        }
        catch (Exception e)
        {
            SeasonTraitsPlugin.Log.LogWarning($"SeasonTraits: font probe failed ({e.Message}), using LegacyRuntime.ttf");
        }
        return Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
    }

    private static void LoadOfficialPrefabs()
    {
        try
        {
            if (_buttonPrefab == null)
            {
                _buttonPrefab = FindLoadedPrefab("PurchaseButton");
            }
            if (_togglePrefab == null)
            {
                _togglePrefab = FindLoadedPrefab("CheckboxImage")
                    ?? FindLoadedPrefab("Checkbox")
                    ?? FindLoadedPrefab("CheckBox")
                    ?? FindLoadedPrefab("Toggle")
                    ?? FindLoadedPrefab("Checkmark")
                    ?? FindLoadedPrefab("CheckMark")
                    ?? Resources.Load<GameObject>("CheckboxImage")
                    ?? Resources.Load<GameObject>("UI/CheckboxImage")
                    ?? Resources.Load<GameObject>("Prefabs/CheckboxImage")
                    ?? Resources.Load<GameObject>("Common/CheckboxImage")
                    ?? Resources.Load<GameObject>("Checkbox")
                    ?? Resources.Load<GameObject>("UI/Checkbox")
                    ?? Resources.Load<GameObject>("Checkmark");
            }
            // broad fallback: any unloaded Toggle prefab whose name suggests a checkbox
            if (_togglePrefab == null)
            {
                var candidates = Resources.FindObjectsOfTypeAll<Toggle>()
                    .Where(t => t != null && t.gameObject != null && !t.gameObject.scene.IsValid())
                    .Select(t => t.gameObject)
                    .Where(g => g.name.IndexOf("Check", StringComparison.OrdinalIgnoreCase) >= 0
                        || g.name.IndexOf("Toggle", StringComparison.OrdinalIgnoreCase) >= 0
                        || g.name.IndexOf("Box", StringComparison.OrdinalIgnoreCase) >= 0)
                    .ToList();
                if (candidates.Count > 0)
                {
                    _togglePrefab = candidates[0];
                    SeasonTraitsPlugin.Log.LogInfo($"SeasonTraits: found checkbox prefab by scan: {_togglePrefab.name}");
                }
            }
            if (_borderPrefab == null)
            {
                _borderPrefab = FindLoadedPrefab("NormalBorder") ?? FindLoadedPrefab("Border") ?? FindLoadedPrefab("SelectedBorder");
            }
            if (_tmpFont == null)
            {
                _tmpFont = Resources.FindObjectsOfTypeAll<TMPro.TextMeshProUGUI>()
                    .FirstOrDefault(t => t != null && t.isActiveAndEnabled && t.font != null)?.font;
            }
            SeasonTraitsPlugin.Log.LogInfo($"SeasonTraits: official prefabs found: button={_buttonPrefab != null} toggle={_togglePrefab != null} border={_borderPrefab != null} tmproFont={_tmpFont != null}");
        }
        catch (Exception e)
        {
            SeasonTraitsPlugin.Log.LogWarning($"SeasonTraits: official prefab probe failed: {e.Message}");
        }
    }

    private static GameObject FindLoadedPrefab(string name)
    {
        return Resources.FindObjectsOfTypeAll<GameObject>()
            .FirstOrDefault(g => g != null && g.name.Equals(name, StringComparison.OrdinalIgnoreCase) && !g.scene.IsValid());
    }

    /// <summary>Instantiate the official DefaultUIButton prefab and wire text + click. False when unavailable.</summary>
    private static bool TryNewOfficialButton(RectTransform parent, string label, Vector2 anchorMin, Vector2 anchorMax, Action onClick)
    {
        if (_buttonPrefab == null)
        {
            return false;
        }
        try
        {
            var clone = Instantiate(_buttonPrefab);
            clone.name = label;
            clone.transform.SetParent(parent, false);
            var rt = (RectTransform)clone.transform;
            rt.anchorMin = anchorMin;
            rt.anchorMax = anchorMax;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
            var dui = clone.GetComponent<EFT.UI.DefaultUIButton>();
            dui.SetHeaderText(label);
            dui.OnClick.AddListener(new UnityEngine.Events.UnityAction(onClick));
            return true;
        }
        catch (Exception e)
        {
            SeasonTraitsPlugin.Log.LogWarning($"SeasonTraits: official button '{label}' failed ({e.Message}), using fallback");
            return false;
        }
    }

    /// <summary>Instantiate the official CheckboxImage prefab as the checkmark shown when selected. Null when unavailable.</summary>
    private static Toggle TryOfficialToggle(RectTransform parent)
    {
        if (_togglePrefab == null)
        {
            return null;
        }
        try
        {
            var clone = Instantiate(_togglePrefab);
            clone.name = "OfficialCheck";
            clone.transform.SetParent(parent, false);
            var t = clone.GetComponent<Toggle>();
            if (t != null)
            {
                t.interactable = false;
                t.transition = Selectable.Transition.None;
            }
            foreach (var img in clone.GetComponentsInChildren<Image>(true))
            {
                img.raycastTarget = false; // row button must receive clicks
            }
            foreach (var tmp in clone.GetComponentsInChildren<TMPro.TMP_Text>(true))
            {
                tmp.gameObject.SetActive(false); // hide any built-in label text
            }
            foreach (var txt in clone.GetComponentsInChildren<Text>(true))
            {
                txt.gameObject.SetActive(false);
            }
            // remove any scroll/pointer event triggers from the toggle prefab: they can swallow wheel events
            foreach (var et in clone.GetComponentsInChildren<EventTrigger>(true))
            {
                Destroy(et);
            }
            clone.SetActive(false); // shown only while the row is selected
            return t;
        }
        catch (Exception e)
        {
            SeasonTraitsPlugin.Log.LogWarning($"SeasonTraits: official toggle failed ({e.Message}), using fallback");
            return null;
        }
    }
    /// <summary>Overlay the official border sprite on the window; stretched 9-slice when possible.</summary>
    private static void TryOfficialFrame(RectTransform window)
    {
        if (_borderPrefab == null)
        {
            return;
        }
        try
        {
            var clone = Instantiate(_borderPrefab);
            clone.name = "OfficialFrame";
            clone.transform.SetParent(window, false);
            var rt = (RectTransform)clone.transform;
            Stretch(rt);
            var img = clone.GetComponent<Image>();
            if (img != null)
            {
                if (img.sprite != null && img.sprite.border.x > 0)
                {
                    img.type = Image.Type.Sliced;
                }
                img.raycastTarget = false;
            }
            var scaler = clone.GetComponent("PixelPerfectSpriteScaler");
            if (scaler != null)
            {
                Destroy(scaler); // fights manual stretch
            }
        }
        catch (Exception e)
        {
            SeasonTraitsPlugin.Log.LogWarning($"SeasonTraits: official frame failed ({e.Message}), keeping outline");
        }
    }
    // ---- ui helpers ----

    private static RectTransform NewRect(string name, Transform parent)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        return (RectTransform)go.transform;
    }

    private static void Stretch(RectTransform rt)
    {
        rt.anchorMin = Vector2.zero;
        rt.anchorMax = Vector2.one;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
    }
    private static Text NewText(string name, Transform parent, Font font, string content, int size, FontStyle style = FontStyle.Normal)
    {
        var rt = NewRect(name, parent);
        var text = rt.gameObject.AddComponent<Text>();
        text.font = font;
        text.fontSize = size;
        text.fontStyle = style;
        text.text = content;
        text.color = Color.white;
        text.horizontalOverflow = HorizontalWrapMode.Wrap;
        text.verticalOverflow = VerticalWrapMode.Overflow;
        text.alignment = TextAnchor.UpperLeft;
        text.raycastTarget = false; // texts must not swallow row clicks
        return text;
    }

    /// <summary>Game-font TMPro label (the font the original prefabs use). Null when no TMPro label is up.</summary>
    private static TMPro.TextMeshProUGUI NewTmp(string name, Transform parent, string content, float size, Color color, bool bold = false)
    {
        if (_tmpFont == null)
        {
            return null;
        }
        var rt = NewRect(name, parent);
        var text = rt.gameObject.AddComponent<TMPro.TextMeshProUGUI>();
        text.font = _tmpFont;
        text.fontSize = size;
        text.fontStyle = bold ? TMPro.FontStyles.Bold : TMPro.FontStyles.Normal;
        text.text = content;
        text.color = color;
        text.raycastTarget = false;
        text.alignment = TMPro.TextAlignmentOptions.MidlineLeft;
        text.enableWordWrapping = true;
        text.overflowMode = TMPro.TextOverflowModes.Overflow;
        return text;
    }

    /// <summary>Manual wheel scrolling: ScrollRect stopped responding to the wheel once the
    /// viewport was sized correctly, so wheel handling is done directly on the content.</summary>
    private class WheelScroll : MonoBehaviour, UnityEngine.EventSystems.IScrollHandler
    {
        public RectTransform Content;
        public Action AfterMove; // must dirty child graphics: content moves alone leave stale culling

        public void OnScroll(UnityEngine.EventSystems.PointerEventData eventData)
        {
            if (Content == null || Content.parent is not RectTransform viewport)
            {
                return;
            }
            var range = Content.rect.height - viewport.rect.height;
            if (range <= 0f)
            {
                return;
            }
            // top-anchored content: scrolling down (delta<0) moves the content UP (pos.y grows)
            var pos = Content.anchoredPosition;
            pos.y = Mathf.Clamp(pos.y - eventData.scrollDelta.y * 40f, 0f, range);
            Content.anchoredPosition = pos;
            AfterMove?.Invoke();
        }
    }

    private static Button NewButton(RectTransform parent, Font font, string label,
        Vector2 anchorMin, Vector2 anchorMax, Color normal, Color textColor, Action onClick)
    {
        var rt = NewRect(label, parent);
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        rt.offsetMin = Vector2.zero;
        rt.offsetMax = Vector2.zero;
        var image = rt.gameObject.AddComponent<Image>();
        image.color = normal;
        var button = rt.gameObject.AddComponent<Button>();
        button.targetGraphic = image;
        button.transition = Selectable.Transition.ColorTint;
        var colors = button.colors;
        colors.highlightedColor = new Color(1f, 1f, 1f, 1f) * 1.25f;
        colors.pressedColor = new Color(0.8f, 0.8f, 0.8f);
        button.colors = colors;
        var text = NewTmp("Label", rt, label, 22, textColor, true);
        if (text != null)
        {
            Stretch(text.rectTransform);
            text.alignment = TMPro.TextAlignmentOptions.Center;
        }
        else
        {
            var textLegacy = NewText("Label", rt, font, label, 22, FontStyle.Bold);
            Stretch(textLegacy.rectTransform);
            textLegacy.alignment = TextAnchor.MiddleCenter;
            textLegacy.color = textColor;
        }
        button.onClick.AddListener(() => onClick());
        return button;
    }

    private static void EnsureEventSystem()
    {
        if (UnityEngine.EventSystems.EventSystem.current != null)
        {
            return;
        }
        var es = new GameObject("SeasonTraitsEventSystem", typeof(UnityEngine.EventSystems.EventSystem),
            typeof(UnityEngine.EventSystems.StandaloneInputModule));
        DontDestroyOnLoad(es);
    }
}
