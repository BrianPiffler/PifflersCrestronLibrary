using System;

namespace PifflersCrestronLibrary.Helper
{
    public class PasscodeManager
    {
        public event EventHandler CorrectPasscodeEntered;
        public event EventHandler IncorrectPasscodeEntered;
        public event EventHandler<string> infoLable;

        private string _normalPasscode;
        private readonly string _adminPasscode;
        private string _currentInput;
        private const int PasscodeLength = 4;

        private int _mode = 0;


        public PasscodeManager(string initialPasscode, string adminPasscode)
        {
            if (initialPasscode.Length != PasscodeLength || adminPasscode.Length != PasscodeLength)
            {
                throw new ArgumentException("Passcodes müssen vierstellig sein.");
            }

            _normalPasscode = initialPasscode;
            _adminPasscode = adminPasscode;
            _currentInput = "";
        }

        public string EnterDigit(char digit)
        {
            if (!Char.IsDigit(digit))
            {
                throw new ArgumentException("Nur Ziffern sind erlaubt.");
            }

            _currentInput += digit;

            if (_currentInput.Length == PasscodeLength)
            {
                if (_mode == 0)
                {
                    if (_currentInput == _normalPasscode)
                    {
                        OnCorrectPasscodeEntered(EventArgs.Empty);
                    }
                    else
                    {
                        OnIncorrectPasscodeEntered(EventArgs.Empty);
                    }

                    ResetInput();
                }
                else if (_mode == 1)
                {
                    if (_currentInput == _adminPasscode)
                    {
                        _mode = 2;
                        OnInfoLable("Enter new Passcode");
                    }
                    else
                    {
                        _mode = 0;
                        OnInfoLable("Enter Passcode");
                    }
                }
                else if (_mode == 2)
                {
                    _mode = 0;
                    _normalPasscode = _currentInput;
                    OnInfoLable("Enter Passcode");
                }

                ResetInput();
            }

            return _currentInput;
        }

        protected virtual void OnCorrectPasscodeEntered(EventArgs e)
        {
            CorrectPasscodeEntered?.Invoke(this, e);
        }

        protected virtual void OnIncorrectPasscodeEntered(EventArgs e)
        {
            IncorrectPasscodeEntered?.Invoke(this, e);
        }

        protected virtual void OnInfoLable(string message)
        {
            infoLable?.Invoke(this, message);
        }

        public void ResetInput()
        {
            _currentInput = "";
        }

        public void changePasscode()
        {
            _mode = 1;
            OnInfoLable("Enter admin Passcode");
        }
    }
}
