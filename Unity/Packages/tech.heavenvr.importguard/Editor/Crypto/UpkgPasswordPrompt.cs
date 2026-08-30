using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace HeavenVR.ImportGuard
{
    /// <summary>
    /// Blocking password prompt (ShowModalUtility, so callers can treat this like
    /// EditorUtility.SaveFilePanel - call it, get an answer back). One field to open
    /// an existing encrypted package; two matching fields to set a new one on export,
    /// since a typo there is unrecoverable once the file is written.
    /// </summary>
    public class UpkgPasswordPrompt : EditorWindow
    {
        string _password = "";
        string _confirm = "";
        bool _confirmMode;
        bool _done;
        string _message;

        public static string PromptExisting(string packageName)
        {
            return Show("Enter password", $"\"{packageName}\" is password-protected.",
                        confirmMode: false);
        }

        public static string PromptNew(string packageName)
        {
            return Show("Set a password",
                        $"Anyone who opens \"{packageName}\" will need this password. It cannot be recovered if lost - there is no reset.",
                        confirmMode: true);
        }

        static string Show(string title, string message, bool confirmMode)
        {
            var window = CreateInstance<UpkgPasswordPrompt>();
            window.titleContent = new GUIContent(title);
            window._confirmMode = confirmMode;
            window._message = message;
            window.minSize = window.maxSize = new Vector2(380, confirmMode ? 190 : 150);
            window.ShowModalUtility();   // blocks until Close()
            return window._done ? window._password : null;
        }

        void CreateGUI()
        {
            var root = rootVisualElement;
            root.style.paddingLeft = 10;
            root.style.paddingRight = 10;
            root.style.paddingTop = 10;
            root.style.paddingBottom = 10;

            var msg = new Label(_message);
            msg.style.whiteSpace = WhiteSpace.Normal;
            msg.style.marginBottom = 8;
            root.Add(msg);

            var pw = new TextField("Password");
            pw.isPasswordField = true;
            pw.RegisterValueChangedCallback(e => _password = e.newValue);
            root.Add(pw);

            TextField confirm = null;
            if (_confirmMode)
            {
                confirm = new TextField("Confirm");
                confirm.isPasswordField = true;
                confirm.RegisterValueChangedCallback(e => _confirm = e.newValue);
                root.Add(confirm);
            }

            var error = new Label();
            error.style.color = new Color(0.95f, 0.4f, 0.4f);
            error.style.marginTop = 4;
            error.style.display = DisplayStyle.None;
            root.Add(error);

            var row = new VisualElement();
            row.style.flexDirection = FlexDirection.Row;
            row.style.marginTop = 12;

            var ok = new Button(() => TrySubmit(error)) { text = "OK" };
            ok.style.flexGrow = 1;
            row.Add(ok);

            var cancel = new Button(Close) { text = "Cancel" };
            cancel.style.flexGrow = 1;
            row.Add(cancel);

            root.Add(row);

            root.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
                    TrySubmit(error);
                else if (e.keyCode == KeyCode.Escape)
                    Close();
            });

            pw.Focus();
        }

        void TrySubmit(Label error)
        {
            if (string.IsNullOrEmpty(_password))
            {
                ShowError(error, "Password can't be empty.");
                return;
            }
            if (_confirmMode && _password != _confirm)
            {
                ShowError(error, "Passwords don't match.");
                return;
            }

            _done = true;
            Close();
        }

        static void ShowError(Label error, string text)
        {
            error.text = text;
            error.style.display = DisplayStyle.Flex;
        }
    }
}
