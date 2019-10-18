namespace PSParallel
{
    using System;
    using System.Globalization;
    using System.Management.Automation.Host;

    /// <summary>
    /// Dummy PSHost implementation
    /// Implements only the manadatory methods to return nothing or defaults
    /// </summary>
    internal class DummyCustomPSHost : PSHost
    {
        /// <summary>
        /// CurrentCulture of Host
        /// </summary>
        private CultureInfo originalCultureInfo =
            System.Threading.Thread.CurrentThread.CurrentCulture;

        /// <summary>
        /// CurrentUICulture of Host
        /// </summary>
        private CultureInfo originalUICultureInfo =
            System.Threading.Thread.CurrentThread.CurrentUICulture;

        /// <summary>
        /// Random guid 
        /// </summary>
        private Guid myId = Guid.NewGuid();

        /// <summary>
        /// Initializes a new instance of the <see cref="DummyCustomPSHost" /> class
        /// </summary>
        public DummyCustomPSHost()
        {
        }

        /// <summary>
        /// originalCultureInfo to Host
        /// </summary>
        public override System.Globalization.CultureInfo CurrentCulture
        {
            get { return this.originalCultureInfo; }
        }

        /// <summary>
        /// CurrentUICulture of Host
        /// </summary>
        public override System.Globalization.CultureInfo CurrentUICulture
        {
            get { return this.originalUICultureInfo; }
        }

        /// <summary>
        /// Guid Of host
        /// </summary>
        public override Guid InstanceId
        {
            get { return this.myId; }
        }

        /// <summary>
        /// Host Name
        /// </summary>
        public override string Name
        {
            get { return "DummyCustomPSHost"; }
        }

        /// <summary>
        /// Dont support UI
        /// </summary>
        public override PSHostUserInterface UI
        {
            get { return null; }
        }

        /// <summary>
        /// Dummy version info
        /// </summary>
        public override Version Version
        {
            get { return new Version(1, 0, 0, 0); }
        }

        /// <summary>
        /// Dont support any other methods
        /// </summary>
        public override void EnterNestedPrompt()
        {
            throw new NotImplementedException(
                "The method or operation is not implemented.");
        }

        /// <summary>
        /// Dont support any other methods
        /// </summary>
        public override void ExitNestedPrompt()
        {
            throw new NotImplementedException(
                "The method or operation is not implemented.");
        }

        /// <summary>
        /// Dont support any other methods
        /// </summary>
        public override void NotifyBeginApplication()
        {
            return;
        }

        /// <summary>
        /// Dont support any other methods
        /// </summary>
        public override void NotifyEndApplication()
        {
            return;
        }

        /// <summary>
        /// Dont support any other methods
        /// </summary>
        /// <param name="exitCode">Dummy parameter, not used</param>
        public override void SetShouldExit(int exitCode)
        {
            return;
        }
    }
}
