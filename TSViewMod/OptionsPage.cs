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
    // Adds a "View Mod" category to the game's Options menu, built from copies of the game's own category button and
    // option rows, so it looks and navigates like the real settings (controller d-pad / stick, mouse +/-).
    // Nothing is added to OptionsMenuController.panelsData; the mod wires the copy itself through the controller's
    // private methods, and inserts the button into its private nav list before the Back button.
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

        private void Update()
        {
            if (Time.unscaledTime < m_nextScan)
            {
                return;
            }
            m_nextScan = Time.unscaledTime + 1f;
            m_injected.RemoveAll(i => i.controller == null);
            foreach (OptionsMenuController controller in Resources.FindObjectsOfTypeAll<OptionsMenuController>())
            {
                if (controller == null || !controller.gameObject.scene.IsValid() || !m_tried.Add(controller.GetInstanceID()))
                {
                    continue;
                }
                try
                {
                    Injected injected = Inject(controller);
                    if (injected != null)
                    {
                        m_injected.Add(injected);
                    }
                }
                catch (Exception e)
                {
                    Loader.Log("options page: failed in " + controller.name + ": " + e);
                }
            }
            foreach (Injected injected in m_injected)
            {
                EnsureNav(injected);
            }
        }

        private static Injected Inject(OptionsMenuController controller)
        {
            List<OptionMenuPanelData> panels = controller.panelsData;
            if (panels == null || panels.Count == 0)
            {
                return null;
            }
            // Template panel: the one with the most ◀ value ▶ rows.
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
                return null;
            }

            // --- category button ---
            Transform lastButton = last.menuButton.transform;
            GameObject buttonGo = Instantiate(last.menuButton.gameObject, lastButton.parent, false);
            buttonGo.name = "TSViewMod_MenuButton";
            StripLocalisation(buttonGo);
            var button = buttonGo.GetComponent<ItemOptionMenuButtonBehaviour>();
            if (lastButton.parent.GetComponent<LayoutGroup>() != null)
            {
                buttonGo.transform.SetSiblingIndex(lastButton.GetSiblingIndex() + 1);
            }
            else if (panels.Count >= 2 && panels[panels.Count - 2].menuButton != null)
            {
                Vector3 step = lastButton.localPosition - panels[panels.Count - 2].menuButton.transform.localPosition;
                buttonGo.transform.localPosition = lastButton.localPosition + step;
                Transform back = controller.backButton != null ? controller.backButton.transform : null;
                if (back != null && back.parent == lastButton.parent && Vector3.Dot(back.localPosition - lastButton.localPosition, step) > 0f)
                {
                    back.localPosition += step;
                }
            }
            button.SetButton("TSViewMod", "View Mod", null);
            SetAllText(buttonGo, "View Mod", null);

            // --- panel ---
            BaseMenuPanelController templatePanel = template.panelController;
            var holder = new GameObject("TSViewMod_Holder");
            holder.SetActive(false);
            GameObject panelGo = Instantiate(templatePanel.gameObject, holder.transform, false);
            panelGo.name = "TSViewMod_PanelViewMod";
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
            panel.Build(DescribeTemplate(templatePanel));

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
            Loader.Log("options page: added View Mod to " + controller.name + " (template " + templatePanel.name + ", " + most + " rows)\n" + Dump(controller.transform, 0, 4));
            return injected;
        }

        // The controller's own nav list is built once from panelsData + Back; slot ours in before Back.
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

        // Where the template panel sits when open (its InitialSetup parks it 2900 px to the right).
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

        private static string DescribeTemplate(BaseMenuPanelController template)
        {
            return template.name;
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

    // The View Mod settings page. Rows are copies of the template panel's first ◀ value ▶ row.
    public class ViewModPanel : BaseMenuPanelController
    {
        private class Row
        {
            public OptionMenuButtonController control;
            public Func<string> value;
            public Action<int> change;
        }

        private readonly List<Row> m_rows = new List<Row>();
        private readonly List<INavItem> m_nav = new List<INavItem>();

        private static Config Cfg => ViewCamera.Instance != null ? ViewCamera.Instance.Config : null;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public void Build(string templateName)
        {
            OptionMenuButtonController[] existing = GetComponentsInChildren<OptionMenuButtonController>(true);
            Array.Sort(existing, (a, b) => b.transform.localPosition.y.CompareTo(a.transform.localPosition.y));
            OptionMenuButtonController first = existing[0];
            float spacing = existing.Length >= 2
                ? Mathf.Abs(existing[0].transform.localPosition.y - existing[1].transform.localPosition.y)
                : ((RectTransform)first.transform).rect.height * 1.15f;

            // Remove every other interactive item copied from the template (other rows, buttons, sliders).
            foreach (NavItemButton nav in GetComponentsInChildren<NavItemButton>(true))
            {
                if (nav != null && !nav.transform.IsChildOf(first.transform) && !first.transform.IsChildOf(nav.transform))
                {
                    Transform item = nav.transform;
                    // Remove the whole row/button object, not just its nav child.
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
            // A single text outside the rows is the page title; several are left alone (see the log dump).
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
                loose[0].SetText("View Mod");
            }

            Loader.Log("options page: template row\n" + OptionsPage.Dump(first.transform, 0, 6));
            AddRows(first, spacing);
            Loader.Log("options page: built " + m_rows.Count + " rows from " + templateName + ", spacing " + spacing.ToString("0") + "\n" + OptionsPage.Dump(transform, 0, 3));
        }

        private void AddRows(OptionMenuButtonController first, float spacing)
        {
            var defs = new List<KeyValuePair<string, Row>>
            {
                Def("Camera", () => Cfg == null ? "-" : ModeName(ViewCamera.Instance.Mode), d =>
                {
                    ViewMode mode = (ViewMode)(((int)ViewCamera.Instance.Mode + d + 3) % 3);
                    ViewCamera.Instance.SetMode(mode);
                    Cfg.StartView = mode.ToString();
                }),
                // Same short-label style and density as the game's own pages; rarer settings stay in TSViewMod.cfg.
                Def("Distance", () => Level(Cfg.ThirdPersonDistance, 0.8f, 0.2f), d => Cfg.ThirdPersonDistance = Step(Cfg.ThirdPersonDistance, d, 0.2f, 0.8f, 2.6f)),
                Def("Height", () => Level(Cfg.PivotHeight, 1.15f, 0.1f), d => Cfg.PivotHeight = Step(Cfg.PivotHeight, d, 0.1f, 1.15f, 1.95f)),
                Def("Shoulder", () => Cycle(new[] { "Auto", "Right", "Left" }, Cfg.ShoulderSide, 0), d => Cfg.ShoulderSide = Cycle(new[] { "Auto", "Right", "Left" }, Cfg.ShoulderSide, d)),
                Def("Field of View", () => Cfg.FieldOfView.ToString("0", Inv), d => Cfg.FieldOfView = Step(Cfg.FieldOfView, d, 5f, 45f, 90f)),
                Def("Sensitivity", () => Level(Cfg.StickSensitivity, 40f, 40f), d =>
                {
                    Cfg.StickSensitivity = Step(Cfg.StickSensitivity, d, 40f, 40f, 400f);
                    Cfg.MouseSensitivity = Cfg.StickSensitivity / 64f; // mouse follows the stick (160 <-> 2.5)
                }),
                Def("Invert Y", () => OnOff(Cfg.InvertY), d => Cfg.InvertY = !Cfg.InvertY),
                Def("R3 Camera Switch", () => OnOff(Cfg.ControllerR3CyclesView), d => Cfg.ControllerR3CyclesView = !Cfg.ControllerR3CyclesView),
                Def("Map Marker", () => OnOff(Cfg.MapMarker), d => Cfg.MapMarker = !Cfg.MapMarker),
                Def("Reset View", () => OnOff(Cfg.InspectReset), d => Cfg.InspectReset = !Cfg.InspectReset),
            };
            Vector3 top = first.transform.localPosition;
            for (int i = 0; i < defs.Count; i++)
            {
                OptionMenuButtonController control = i == 0 ? first : Instantiate(first.gameObject, first.transform.parent, false).GetComponent<OptionMenuButtonController>();
                control.name = "TSViewMod_Row_" + i;
                control.transform.localPosition = top + Vector3.down * spacing * i;
                Row row = defs[i].Value;
                row.control = control;
                // Label = the row's text that isn't the value and isn't part of the ◀ ▶ buttons.
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

        // Settings shown as 1..10 steps like the game's own sliders, rather than metres/raw numbers.
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
