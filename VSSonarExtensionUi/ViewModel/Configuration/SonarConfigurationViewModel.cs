﻿// --------------------------------------------------------------------------------------------------------------------
// <copyright file="SonarConfigurationViewModel.cs" company="">
//   
// </copyright>
// <summary>
//   The sonar configuration view viewModel.
// </summary>
// --------------------------------------------------------------------------------------------------------------------
namespace VSSonarExtensionUi.ViewModel.Configuration
{
    using System;
    using System.Windows.Controls;
    using System.Windows.Media;

    using CredentialManagement;

    using ExtensionTypes;

    using GalaSoft.MvvmLight.Command;

    using SonarRestService;

    using VSSonarExtensionUi.View;

    using VSSonarPlugins;

    /// <summary>
    ///     The sonar configuration view viewModel.
    /// </summary>
    public class SonarConfigurationViewModel : IViewModelBase, IOptionsViewModelBase
    {
        #region Fields

        /// <summary>
        ///     The viewModel.
        /// </summary>
        private readonly VSonarQubeOptionsViewModel viewModel;

        /// <summary>
        ///     The rest service.
        /// </summary>
        private readonly ISonarRestService restService;

        /// <summary>
        /// The visual studio helper.
        /// </summary>
        private readonly IVsEnvironmentHelper visualStudioHelper;

        #endregion

        #region Constructors and Destructors

        /// <summary>
        /// Initializes a new instance of the <see cref="SonarConfigurationViewModel"/> class.
        /// </summary>
        /// <param name="viewModel">
        /// The viewModel.
        /// </param>
        /// <param name="restService">
        /// The rest service.
        /// </param>
        /// <param name="helper">
        /// The vs Helper.
        /// </param>
        public SonarConfigurationViewModel(VSonarQubeOptionsViewModel viewModel, ISonarRestService restService, IVsEnvironmentHelper helper)
        {
            this.Header = "Connection";
            this.viewModel = viewModel;
            this.restService = restService;
            this.visualStudioHelper = helper;

            this.TestConnectionCommand = new RelayCommand<object>(this.OnTestAndSavePassword);

            this.BackGroundColor = Colors.Black;
            this.ForeGroundColor = Colors.White;

            this.GetCredentials();
            this.ResetDefaults();
        }

        #endregion

        #region Public Properties

        /// <summary>
        ///     Gets or sets the back ground color.
        /// </summary>
        public Color BackGroundColor { get; set; }

        /// <summary>
        ///     Gets or sets a value indicating whether disable editor tags.
        /// </summary>
        public bool DisableEditorTags { get; set; }

        /// <summary>
        ///     Gets or sets the fore ground color.
        /// </summary>
        public Color ForeGroundColor { get; set; }

        /// <summary>
        ///     Gets or sets the header.
        /// </summary>
        public string Header { get; set; }

        /// <summary>
        ///     Gets or sets a value indicating whether is connect at start on.
        /// </summary>
        public bool IsConnectAtStartOn { get; set; }

        /// <summary>
        ///     Gets or sets the password.
        /// </summary>
        public string Password { get; set; }

        /// <summary>
        ///     Gets or sets the server address.
        /// </summary>
        public string ServerAddress { get; set; }

        /// <summary>
        ///     Gets or sets the status message.
        /// </summary>
        public string StatusMessage { get; set; }

        /// <summary>
        ///     Gets or sets the test connection command.
        /// </summary>
        public RelayCommand<object> TestConnectionCommand { get; set; }

        /// <summary>
        ///     Gets or sets the user connection config.
        /// </summary>
        public ISonarConfiguration UserConnectionConfig { get; set; }

        /// <summary>
        ///     Gets or sets the user name.
        /// </summary>
        public string UserName { get; set; }

        #endregion

        #region Public Methods and Operators

        /// <summary>
        ///     The apply.
        /// </summary>
        public void Apply()
        {
            if (this.IsConnectAtStartOn)
            {
                this.viewModel.Vsenvironmenthelper.WriteOptionInApplicationData("VSSonarQubeConfig", "AutoConnectAtStart", "true");
            }
            else
            {
                this.viewModel.Vsenvironmenthelper.WriteOptionInApplicationData("VSSonarQubeConfig", "AutoConnectAtStart", "false");
            }
        }

        /// <summary>
        /// The end data association.
        /// </summary>
        public void EndDataAssociation()
        {
        }

        /// <summary>
        ///     The exit.
        /// </summary>
        public void Exit()
        {
            this.GetCredentials();
            this.ResetDefaults();
        }

        /// <summary>
        /// The on disable editor tags changed.
        /// </summary>
        public void OnDisableEditorTagsChanged()
        {
            this.visualStudioHelper.WriteOptionInApplicationData("SonarOptionsGeneral", "DisableEditorTags", this.DisableEditorTags ? "TRUE" : "FALSE");
        }

        /// <summary>
        ///     The save and close.
        /// </summary>
        public void SaveAndClose()
        {
            this.Apply();
        }

        /// <summary>
        /// The update colours.
        /// </summary>
        /// <param name="background">
        /// The background.
        /// </param>
        /// <param name="foreground">
        /// The foreground.
        /// </param>
        public void UpdateColours(Color background, Color foreground)
        {
            this.BackGroundColor = background;
            this.ForeGroundColor = foreground;
        }

        #endregion

        #region Methods

        /// <summary>
        ///     The get credentials.
        /// </summary>
        private void GetCredentials()
        {
            var cm = new Credential { Target = "VSSonarQubeExtension", };

            if (!cm.Exists())
            {
                return;
            }

            cm.Load();

            string address = this.viewModel.Vsenvironmenthelper.ReadOptionFromApplicationData("VSSonarQubeConfig", "ServerAddress");

            if (string.IsNullOrEmpty(address))
            {
                return;
            }

            this.UserConnectionConfig = new ConnectionConfiguration(
                address, 
                cm.Username, 
                ConnectionConfiguration.ConvertToUnsecureString(cm.SecurePassword));
            this.UserName = cm.Username;
            this.ServerAddress = address;
            this.Password = ConnectionConfiguration.ConvertToUnsecureString(cm.SecurePassword);
        }

        /// <summary>
        /// The on test and save password.
        /// </summary>
        /// <param name="obj">
        /// The obj.
        /// </param>
        private void OnTestAndSavePassword(object obj)
        {
            var passwordBox = obj as PasswordBox;
            if (passwordBox != null)
            {
                string password = passwordBox.Password;

                this.UserConnectionConfig = new ConnectionConfiguration(this.ServerAddress, this.UserName, password);

                try
                {
                    if (this.restService.AuthenticateUser(this.UserConnectionConfig))
                    {
                        this.StatusMessage = "Authenticated";
                        this.SetCredentials(this.UserName, password);
                    }
                    else
                    {
                        UserExceptionMessageBox.ShowException("Cannot Authenticate", null);
                    }
                }
                catch (Exception ex)
                {
                    UserExceptionMessageBox.ShowException("Cannot Authenticate", ex);
                }
            }
        }

        /// <summary>
        ///     The reset defaults.
        /// </summary>
        private void ResetDefaults()
        {
            string isConnectAuto = this.viewModel.Vsenvironmenthelper.ReadOptionFromApplicationData("VSSonarQubeConfig", "AutoConnectAtStart");

            if (string.IsNullOrEmpty(isConnectAuto))
            {
                this.IsConnectAtStartOn = true;
                return;
            }

            this.IsConnectAtStartOn = isConnectAuto.Equals("true");
        }

        /// <summary>
        /// The set credentials.
        /// </summary>
        /// <param name="userName">
        /// The user name.
        /// </param>
        /// <param name="password">
        /// The password.
        /// </param>
        private void SetCredentials(string userName, string password)
        {
            this.viewModel.Vsenvironmenthelper.WriteOptionInApplicationData("VSSonarQubeConfig", "ServerAddress", this.ServerAddress);
            var cm = new Credential
                         {
                             Target = "VSSonarQubeExtension", 
                             PersistanceType = PersistanceType.Enterprise, 
                             Username = userName, 
                             SecurePassword = ConnectionConfiguration.ConvertToSecureString(password)
                         };
            cm.Save();
        }

        #endregion
    }
}