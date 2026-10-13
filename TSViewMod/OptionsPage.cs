using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace TSViewMod
{
    // Adds "View Mod" and "Keyboard & Mouse" categories to Options, cloned from the game's own button and rows so
    // they look and navigate natively. Wired through the controller's private methods and nav list (not panelsData).
    public class OptionsPage : MonoBehaviour
    {
        private class Injected
        {
            public OptionsMenuController controller;
            public ItemOptionMenuButtonBehaviour button;
            public ViewModPanel panel;
        }

        private static readonly BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        private readonly List<Injected> m_injected = new List<Injected>();
        private readonly HashSet<int> m_tried = new HashSet<int>();
        private float m_nextScan;
        private float m_scanUntil;

        // The options menus are part of the menu and gameplay scenes, so the (costly) search for them only runs for a
        // few seconds after each scene load.
        private void OnEnable()
        {
            UnityEngine.SceneManagement.SceneManager.sceneLoaded += OnSceneLoaded;
            m_scanUntil = Time.unscaledTime + 5f;
        }

        private void OnDisable()
        {
            UnityEngine.SceneManagement.SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void OnSceneLoaded(UnityEngine.SceneManagement.Scene scene, UnityEngine.SceneManagement.LoadSceneMode mode)
        {
            m_scanUntil = Time.unscaledTime + 5f;
        }

        private void Update()
        {
            if (Time.unscaledTime < m_nextScan)
            {
                return;
            }
            m_nextScan = Time.unscaledTime + 0.5f;
            m_injected.RemoveAll(i => i.controller == null);
            // The menu builds its nav list when first opened, which can be long after injection: re-add ours (cheap).
            foreach (Injected injected in m_injected)
            {
                EnsureNav(injected);
            }
            if (Time.unscaledTime > m_scanUntil)
            {
                return;
            }
            foreach (OptionsMenuController controller in Resources.FindObjectsOfTypeAll<OptionsMenuController>())
            {
                if (controller == null || !controller.gameObject.scene.IsValid() || !m_tried.Add(controller.GetInstanceID()))
                {
                    continue;
                }
                try
                {
                    m_injected.AddRange(Inject(controller));
                }
                catch (Exception e)
                {
                    Loader.Log("options page: failed in " + controller.name + ": " + e);
                }
            }
        }

        private static List<Injected> Inject(OptionsMenuController controller)
        {
            var added = new List<Injected>();
            List<OptionMenuPanelData> panels = controller.panelsData;
            if (panels == null || panels.Count == 0)
            {
                return added;
            }
            // Template: the panel with the most rows.
            OptionMenuPanelData template = null;
            int most = 0;
            foreach (OptionMenuPanelData data in panels)
            {
                int rows = data?.panelController != null ? data.panelController.GetComponentsInChildren<OptionMenuButtonController>(true).Length : 0;
                if (rows > most)
                {
                    most = rows;
                    template = data;
                }
            }
            OptionMenuPanelData last = panels[panels.Count - 1];
            if (template == null || last.menuButton == null)
            {
                Loader.Log("options page: no template panel in " + controller.name);
                return added;
            }
            Vector3 step = panels.Count >= 2 && panels[panels.Count - 2].menuButton != null
                ? last.menuButton.transform.localPosition - panels[panels.Count - 2].menuButton.transform.localPosition
                : Vector3.zero;
            Transform anchor = last.menuButton.transform;
            foreach (PanelKind kind in new[] { PanelKind.Settings, PanelKind.Keys })
            {
                Injected injected = AddCategory(controller, template.panelController, last.menuButton.gameObject, anchor, step, kind);
                added.Add(injected);
                anchor = injected.button.transform;
            }
            Loader.Log("options page: added View Mod + Keyboard & Mouse to " + controller.name + " (template " + template.panelController.name + ", " + most + " rows)\n" + Dump(controller.transform, 0, 4));
            return added;
        }

        private static Injected AddCategory(OptionsMenuController controller, BaseMenuPanelController templatePanel, GameObject buttonTemplate, Transform anchor, Vector3 step, PanelKind kind)
        {
            string title = kind == PanelKind.Keys ? "Keyboard & Mouse" : "View Mod";

            // --- category button ---
            GameObject buttonGo = Instantiate(buttonTemplate, anchor.parent, false);
            buttonGo.name = "TSViewMod_MenuButton_" + kind;
            StripLocalisation(buttonGo);
            var button = buttonGo.GetComponent<ItemOptionMenuButtonBehaviour>();
            if (anchor.parent.GetComponent<LayoutGroup>() != null)
            {
                buttonGo.transform.SetSiblingIndex(anchor.GetSiblingIndex() + 1);
            }
            else if (step != Vector3.zero)
            {
                buttonGo.transform.localPosition = anchor.localPosition + step;
                Transform back = controller.backButton != null ? controller.backButton.transform : null;
                if (back != null && back.parent == anchor.parent && Vector3.Dot(back.localPosition - anchor.localPosition, step) > 0f)
                {
                    back.localPosition += step;
                }
            }
            button.SetButton("TSViewMod_" + kind, title, null);
            SetAllText(buttonGo, title, null);

            // --- panel ---
            var holder = new GameObject("TSViewMod_Holder");
            holder.SetActive(false);
            GameObject panelGo = Instantiate(templatePanel.gameObject, holder.transform, false);
            panelGo.name = "TSViewMod_Panel" + kind;
            foreach (BaseMenuPanelController old in panelGo.GetComponents<BaseMenuPanelController>())
            {
                DestroyImmediate(old);
            }
            StripLocalisation(panelGo);
            panelGo.transform.SetParent(templatePanel.transform.parent, false);
            panelGo.transform.SetSiblingIndex(templatePanel.transform.GetSiblingIndex() + 1);
            panelGo.transform.localPosition = PanelHome(templatePanel);
            Destroy(holder);

            var panel = panelGo.AddComponent<ViewModPanel>();
            panel.Build(templatePanel.name, title, kind);

            // --- wiring, as OptionsMenuController.Awake does for its own panels ---
            var entry = new OptionMenuPanelData { menuButton = button, panelController = panel };
            MethodInfo optionPressed = typeof(OptionsMenuController).GetMethod("OptionPressed", Any);
            MethodInfo pressedBack = typeof(OptionsMenuController).GetMethod("OnPressedBackOnPanel", Any);
            button.AddPressCallback(_ => optionPressed.Invoke(controller, new object[] { entry, null, true }));
            panel.InitialSetup();
            panel.SetNavParent(button.navButton);
            panel.OnUser_PressedBackOnPanel = () => pressedBack.Invoke(controller, new object[] { entry });

            var injected = new Injected { controller = controller, button = button, panel = panel };
            EnsureNav(injected);
            return injected;
        }

        // Insert our nav item before Back.
        private static void EnsureNav(Injected injected)
        {
            var list = typeof(OptionsMenuController).GetField("NavMenuItems", Any)?.GetValue(injected.controller) as List<INavItem>;
            if (list == null || injected.button == null || list.Contains(injected.button.navButton))
            {
                return;
            }
            int back = injected.controller.backButton != null ? list.IndexOf(injected.controller.backButton.navButton) : -1;
            list.Insert(back >= 0 ? back : list.Count, injected.button.navButton);
        }

        // Open position of the template (InitialSetup parks panels 2900 px right).
        private static Vector3 PanelHome(BaseMenuPanelController template)
        {
            FieldInfo initPos = typeof(BaseMenuPanelController).GetField("initPos", Any);
            FieldInfo endPos = typeof(BaseMenuPanelController).GetField("endPos", Any);
            if (initPos != null && endPos != null && (Vector2)endPos.GetValue(template) != Vector2.zero)
            {
                Vector2 home = (Vector2)initPos.GetValue(template);
                return new Vector3(home.x, home.y, template.transform.localPosition.z);
            }
            return template.transform.localPosition;
        }

        private static void StripLocalisation(GameObject go)
        {
            foreach (MultilanguageUIAutotext text in go.GetComponentsInChildren<MultilanguageUIAutotext>(true))
            {
                DestroyImmediate(text);
            }
        }

        public static void SetAllText(GameObject go, string value, TMP_Text except)
        {
            foreach (TMP_Text text in go.GetComponentsInChildren<TMP_Text>(true))
            {
                if (text != except)
                {
                    text.SetText(value);
                }
            }
        }

        public static string Dump(Transform t, int depth, int maxDepth)
        {
            var sb = new StringBuilder();
            sb.Append(' ', depth * 2).Append(t.name).Append(t.gameObject.activeSelf ? "" : " (off)");
            if (t is RectTransform rt)
            {
                sb.Append(" pos ").Append(rt.localPosition.ToString("0")).Append(" size ").Append(rt.rect.size.ToString("0"));
            }
            TMP_Text tmp = t.GetComponent<TMP_Text>();
            if (tmp != null)
            {
                sb.Append(" \"").Append(tmp.text).Append('"');
            }
            foreach (Component c in t.GetComponents<MonoBehaviour>())
            {
                if (c != null && !(c is Graphic))
                {
                    sb.Append(" [").Append(c.GetType().Name).Append(']');
                }
            }
            sb.Append('\n');
            if (depth < maxDepth)
            {
                foreach (Transform child in t)
                {
                    sb.Append(Dump(child, depth + 1, maxDepth));
                }
            }
            return sb.ToString();
        }
    }

    public enum PanelKind
    {
        Settings,
        Keys
    }

    public class ViewModPanel : BaseMenuPanelController
    {
        private class Row
        {
            public OptionMenuButtonController control;
            public Func<string> value;
            public Action<int> change;
        }

        private readonly List<Row> m_rows = new List<Row>();
        private static bool s_comingSoon;
        private readonly List<INavItem> m_nav = new List<INavItem>();

        private static Config Cfg => ViewCamera.Instance != null ? ViewCamera.Instance.Config : null;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        private PanelKind m_kind;

        public void Build(string templateName, string title, PanelKind kind)
        {
            m_kind = kind;
            OptionMenuButtonController[] existing = GetComponentsInChildren<OptionMenuButtonController>(true);
            Array.Sort(existing, (a, b) => b.transform.localPosition.y.CompareTo(a.transform.localPosition.y));
            OptionMenuButtonController first = existing[0];
            float spacing = existing.Length >= 2
                ? Mathf.Abs(existing[0].transform.localPosition.y - existing[1].transform.localPosition.y)
                : ((RectTransform)first.transform).rect.height * 1.15f;

            // Remove the template's other rows and buttons.
            foreach (NavItemButton nav in GetComponentsInChildren<NavItemButton>(true))
            {
                if (nav != null && !nav.transform.IsChildOf(first.transform) && !first.transform.IsChildOf(nav.transform))
                {
                    Transform item = nav.transform;
                    while (item.parent != null && item.parent != first.transform.parent && item.parent != transform)
                    {
                        item = item.parent;
                    }
                    if (item != first.transform && !first.transform.IsChildOf(item))
                    {
                        DestroyImmediate(item.gameObject);
                    }
                }
            }
            // A single text outside the rows is the page title.
            var loose = new List<TMP_Text>();
            foreach (TMP_Text text in GetComponentsInChildren<TMP_Text>(true))
            {
                if (!text.transform.IsChildOf(first.transform))
                {
                    loose.Add(text);
                }
            }
            if (loose.Count == 1)
            {
                loose[0].SetText(title);
            }

            Loader.Log("options page: template row\n" + OptionsPage.Dump(first.transform, 0, 6));
            if (kind == PanelKind.Keys)
            {
                AddKeyRows(first, spacing);
            }
            else
            {
                AddRows(first, spacing);
            }
            Loader.Log("options page: built " + m_rows.Count + " rows from " + templateName + ", spacing " + spacing.ToString("0") + "\n" + OptionsPage.Dump(transform, 0, 3));
        }

        private void AddRows(OptionMenuButtonController first, float spacing)
        {
            var defs = new List<KeyValuePair<string, Row>>
            {
                // First person shows "Coming Soon" and keeps the current camera.
                Def("Camera", () => s_comingSoon ? "First Person (Coming Soon)" : ModeName(ViewCamera.Instance.Mode), d =>
                {
                    ViewMode current = s_comingSoon ? ViewMode.FirstPerson : ViewCamera.Instance.Mode;
                    ViewMode mode = (ViewMode)(((int)current + d + 3) % 3);
                    s_comingSoon = mode == ViewMode.FirstPerson && !ViewCamera.FirstPersonAvailable;
                    if (!s_comingSoon)
                    {
                        ViewCamera.Instance.SetMode(mode);
                        Cfg.StartView = mode.ToString();
                    }
                }),
                // Rarer settings stay in TSViewMod.cfg.
                Def("Distance", () => Level(Cfg.ThirdPersonDistance, 0.8f, 0.2f), d => Cfg.ThirdPersonDistance = Step(Cfg.ThirdPersonDistance, d, 0.2f, 0.8f, 2.6f)),
                Def("Height", () => Level(Cfg.PivotHeight, 1.15f, 0.1f), d => Cfg.PivotHeight = Step(Cfg.PivotHeight, d, 0.1f, 1.15f, 1.95f)),
                Def("Shoulder", () => Cycle(new[] { "Auto", "Right", "Left" }, Cfg.ShoulderSide, 0), d => Cfg.ShoulderSide = Cycle(new[] { "Auto", "Right", "Left" }, Cfg.ShoulderSide, d)),
                Def("Field of View", () => Cfg.FieldOfView.ToString("0", Inv), d => Cfg.FieldOfView = Step(Cfg.FieldOfView, d, 5f, 45f, 90f)),
                Def("Stick Speed", () => Level(Cfg.StickSensitivity, 40f, 40f), d => Cfg.StickSensitivity = Step(Cfg.StickSensitivity, d, 40f, 40f, 400f)),
                Def("Mouse Speed", () => Level(Cfg.MouseSensitivity, 0.5f, 0.5f), d => Cfg.MouseSensitivity = Step(Cfg.MouseSensitivity, d, 0.5f, 0.5f, 5f)),
                // One row for both devices keeps the game's 10-row density.
                Def("Invert Y", () => InvertLabel(), d =>
                {
                    int i = ((Cfg.InvertY ? 1 : 0) + (Cfg.MouseInvertY ? 2 : 0) + (d == 0 ? 1 : d) + 4) % 4;
                    Cfg.InvertY = (i & 1) != 0;
                    Cfg.MouseInvertY = (i & 2) != 0;
                }),
                Def("Auto Follow", () => OnOff(Cfg.AutoFollow), d => Cfg.AutoFollow = !Cfg.AutoFollow),
                Def("Map Marker", () => OnOff(Cfg.MapMarker), d => Cfg.MapMarker = !Cfg.MapMarker),
            };
            Vector3 top = first.transform.localPosition;
            for (int i = 0; i < defs.Count; i++)
            {
                OptionMenuButtonController control = i == 0 ? first : Instantiate(first.gameObject, first.transform.parent, false).GetComponent<OptionMenuButtonController>();
                control.name = "TSViewMod_Row_" + i;
                control.transform.localPosition = top + Vector3.down * spacing * i;
                Row row = defs[i].Value;
                row.control = control;
                // Label = the row's text outside the value and the arrow buttons.
                foreach (TMP_Text text in control.GetComponentsInChildren<TMP_Text>(true))
                {
                    if (text != control.selectorText && !Inside(text, control.Button_Minus) && !Inside(text, control.Button_Plus) && !Inside(text, control.Button_Center))
                    {
                        text.SetText(defs[i].Key);
                    }
                }
                m_options.Add(new OptionMenuButtonBaseBehaviour(control, d =>
                {
                    if (Cfg == null || d == 0 && !(row.value() == "On" || row.value() == "Off"))
                    {
                        return;
                    }
                    row.change(d == 0 ? 1 : d);
                    Cfg.Save();
                    Refresh(row);
                }, () => Refresh(row)));
                m_rows.Add(row);
                m_nav.Add(control.GetNavItem());
                Refresh(row);
            }
        }

        private static bool Inside(Component child, Component parent)
        {
            return parent != null && child.transform.IsChildOf(parent.transform);
        }

        // Shown as 1..10 steps like the game's own sliders.
        private static string InvertLabel()
        {
            return Cfg.InvertY ? (Cfg.MouseInvertY ? "Both" : "Stick") : (Cfg.MouseInvertY ? "Mouse" : "Off");
        }

        private static string Level(float value, float min, float step)
        {
            return (Mathf.RoundToInt((value - min) / step) + 1).ToString(Inv);
        }

        private static KeyValuePair<string, Row> Def(string label, Func<string> value, Action<int> change)
        {
            return new KeyValuePair<string, Row>(label, new Row { value = value, change = change });
        }

        private static void Refresh(Row row)
        {
            if (row.control != null && Cfg != null)
            {
                row.control.SetText(row.value());
            }
        }

        private static string ModeName(ViewMode mode)
        {
            return mode == ViewMode.ThirdPerson ? "Third Person" : mode == ViewMode.FirstPerson ? "First Person" : "Original";
        }

        private static string OnOff(bool b)
        {
            return b ? "On" : "Off";
        }

        private static float Step(float value, int d, float step, float min, float max)
        {
            return Mathf.Clamp(Mathf.Round((value + d * step) / step) * step, min, max);
        }

        private static string Cycle(string[] values, string current, int d)
        {
            int i = Array.FindIndex(values, v => string.Equals(v, current, StringComparison.OrdinalIgnoreCase));
            return values[((i < 0 ? 0 : i) + d + values.Length) % values.Length];
        }

        public override void InitialSetup()
        {
            base.InitialSetup();
            SetNavItems(m_nav);
        }

        protected override void OnEnter()
        {
            s_comingSoon = false;
            if (m_kind == PanelKind.Keys)
            {
                ShowKeys();
            }
        }

        // ---------- Keyboard & Mouse page ----------
        // A page selector row, then read-only "action: keys" rows. Game keys are read live from Rewired, so they
        // stay right if the game's bindings change.
        private static readonly string[] KeyPages = { "Movement & Combat", "Menus & Items", "View Mod" };
        private int m_keyPage;
        private OptionMenuButtonController m_pageRow;
        private readonly List<OptionMenuButtonController> m_keyRows = new List<OptionMenuButtonController>();

        private void AddKeyRows(OptionMenuButtonController first, float spacing)
        {
            Vector3 top = first.transform.localPosition;
            for (int i = 0; i < 8; i++)
            {
                OptionMenuButtonController control = i == 0 ? first : Instantiate(first.gameObject, first.transform.parent, false).GetComponent<OptionMenuButtonController>();
                control.name = "TSViewMod_KeyRow_" + i;
                control.transform.localPosition = top + Vector3.down * spacing * i;
                if (i == 0)
                {
                    m_pageRow = control;
                    SetLabel(control, "Show");
                    m_options.Add(new OptionMenuButtonBaseBehaviour(control, d =>
                    {
                        m_keyPage = (m_keyPage + (d == 0 ? 1 : d) + KeyPages.Length) % KeyPages.Length;
                        ShowKeys();
                    }, ShowKeys));
                    m_nav.Add(control.GetNavItem());
                }
                else
                {
                    // Read-only: no arrows, not in the navigation.
                    foreach (Component arrow in new Component[] { control.Button_Minus, control.Button_Plus })
                    {
                        if (arrow != null)
                        {
                            arrow.gameObject.SetActive(false);
                        }
                    }
                    m_keyRows.Add(control);
                }
            }
            ShowKeys();
        }

        private void ShowKeys()
        {
            if (m_pageRow == null)
            {
                return;
            }
            m_pageRow.SetText(KeyPages[m_keyPage]);
            List<KeyValuePair<string, string>> entries = KeyBindings.Page(m_keyPage);
            for (int i = 0; i < m_keyRows.Count; i++)
            {
                bool used = i < entries.Count;
                m_keyRows[i].gameObject.SetActive(used);
                if (used)
                {
                    SetLabel(m_keyRows[i], entries[i].Key);
                    m_keyRows[i].SetText(entries[i].Value);
                }
            }
        }

        private static void SetLabel(OptionMenuButtonController control, string label)
        {
            foreach (TMP_Text text in control.GetComponentsInChildren<TMP_Text>(true))
            {
                if (text != control.selectorText && !Inside(text, control.Button_Minus) && !Inside(text, control.Button_Plus) && !Inside(text, control.Button_Center))
                {
                    text.SetText(label);
                }
            }
        }

        public override INavItem GetMainNavItem()
        {
            return m_nav.Count > 0 ? m_nav[0] : null;
        }

        public override void SetNavParent(NavItemButton menuButtonNavButton)
        {
            foreach (INavItem item in m_nav)
            {
                if (item is NavItemButton nav)
                {
                    nav.navigationReferences.backward = menuButtonNavButton;
                }
            }
        }
    }
}
