using App.ControlPanel.Engine;
using App.ControlPanel.Engine.Entities;
using App.ControlPanel.Engine.Models;
using Azure.Identity;
using Azure.ResourceManager;
using Common.Entities.Installer;
using DataUtils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace App.ControlPanel.Frames.InstallWizardPages
{
    public partial class AzureBaseConfigControl : UserControl
    {
        private SubscriptionInputMethod _subscriptionFormInputMethod;
        const string NO_REGION_SELECTION_TEXT = "---";
        const string PERF_TIER_TESTING = "Testing";
        const string PERF_TIER_PROD = "Production";
        public AzureBaseConfigControl()
        {
            InitializeComponent();
            cmbEnvType.Items.Clear();
            cmbEnvType.Items.AddRange(new string[] { PERF_TIER_TESTING, PERF_TIER_PROD });
        }

        public event EventHandler<bool> LoadingSubscriptionStateChange;

        public Func<AppRegistrationCredentials> OnNeedAppRegistrationCredentials;

        private AppRegistrationCredentials _refreshSubsAccount;
        void EnsureAzureLocationsPopulated()
        {
            if (cmbLocation.Items.Count > 0)
            {
                return;
            }
            foreach (var region in AzurePublicCloudEnumerator.GetAzureLocations().OrderBy(v => v.Name))
            {
                cmbLocation.Items.Add(region.Name);
            }
            cmbLocation.Items.Add(NO_REGION_SELECTION_TEXT);


            SetSubscriptionInputMethod(SubscriptionInputMethod.Picker);

            grpSubDetails.Location = cmbSubscriptions.Location;
        }

        private void btnRefreshUISubs()
        {
            // Get the credentials from parent
            this._refreshSubsAccount = OnNeedAppRegistrationCredentials?.Invoke();

            var accountErrors = _refreshSubsAccount.GetValidationErrors();
            if (accountErrors.Count > 0)
            {
                MessageBox.Show("Please enter valid credentials to be able to refresh Azure subscriptions.", "Can't Refresh", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            refreshSubbackgroundWorker.RunWorkerAsync();
            LoadingSubscriptionStateChange?.Invoke(this, true);
        }

        private void refreshSubbackgroundWorker_DoWork(object sender, System.ComponentModel.DoWorkEventArgs e)
        {
            e.Result = GetSubscriptions();
        }

        #region Props

        public AzureSubscription AzureSubscription
        {
            get
            {
                if (_subscriptionFormInputMethod == SubscriptionInputMethod.Picker)
                {
                    return cmbSubscriptions.SelectedItem as AzureSubscription;
                }
                else
                {
                    return new AzureSubscription(txtSubscriptionId.Text, txtSubscriptionName.Text);
                }
            }
            set
            {
                if (value != null && value.IsValidSubscription)
                {
                    if (!cmbSubscriptions.Items.Contains(value))
                    {
                        cmbSubscriptions.Items.Add(value);
                    }
                    cmbSubscriptions.SelectedItem = value;
                }
                else
                {
                    cmbSubscriptions.SelectedItem = null;
                }
            }
        }

        public string ResourceGroup
        {
            get { return txtResourceGroup.Text; }
            set { txtResourceGroup.Text = value; }
        }
        public string AzureLocationString
        {
            get
            {
                var location = string.Empty;
                if (cmbLocation.SelectedItem != null)
                {
                    location = cmbLocation.SelectedItem.ToString();
                }
                return location;
            }
            set
            {
                EnsureAzureLocationsPopulated();
                if (string.IsNullOrEmpty(value))
                {
                    cmbLocation.SelectedItem = NO_REGION_SELECTION_TEXT;
                }
                else
                {
                    cmbLocation.SelectedItem = value;
                }
            }
        }

        public EnvironmentTypeEnum EnvironmentType
        {
            get
            {
                if (cmbEnvType.SelectedItem?.ToString() == PERF_TIER_PROD)
                {
                    return EnvironmentTypeEnum.Production;
                }
                else
                {
                    return EnvironmentTypeEnum.Testing;
                }
            }
            set
            {
                if (value == EnvironmentTypeEnum.Production)
                {
                    cmbEnvType.SelectedItem = PERF_TIER_PROD;
                }
                else
                {
                    cmbEnvType.SelectedItem = PERF_TIER_TESTING;
                }
                RefreshPerformanceTierUI();
            }
        }

        public List<AzTag> Tags
        {
            get
            {
                return tagsEditor.Tags;
            }
            set
            {
                tagsEditor.Tags = value;
            }
        }

        #endregion

        /// <summary>
        /// Retrieves list of Azure subscriptions on worker thread
        /// </summary>
        List<AzureSubscription> GetSubscriptions()
        {
            var subs = new List<AzureSubscription>();

            var creds = new ClientSecretCredential(this._refreshSubsAccount.DirectoryId, _refreshSubsAccount.ClientId, this._refreshSubsAccount.Secret);
            var client = new ArmClient(creds);

            foreach (var sub in client.GetSubscriptions())
            {
                subs.Add(new AzureSubscription(sub.Data.SubscriptionId, sub.Data.DisplayName));
            }

            return subs;
        }

        private void refreshSubbackgroundWorker_RunWorkerCompleted(object sender, System.ComponentModel.RunWorkerCompletedEventArgs e)
        {
            LoadingSubscriptionStateChange?.Invoke(this, false);
            if (e.Error != null)
            {
                var rootError = CommonExceptionHandler.GetRootException(e.Error);
                MessageBox.Show(rootError.Message, "Couldn't Refresh Subscriptions", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
            }
            else
            {
                if (e.Result is List<AzureSubscription>)
                {
                    List<AzureSubscription> results = e.Result as List<AzureSubscription>;
                    cmbSubscriptions.Items.Clear();
                    if (results.Count > 0)
                    {
                        foreach (var sub in results)
                        {
                            cmbSubscriptions.Items.Add(sub);
                        }

                        cmbSubscriptions.SelectedIndex = 0;
                    }
                    else
                    {
                        MessageBox.Show("No subscriptions found. Does the installer account have permissions to read the subscription details?", "Refresh Subscriptions", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                    }
                }
                else
                {
                    MessageBox.Show("Unexpected results from query.", "Couldn't Refresh Subscriptions", MessageBoxButtons.OK, MessageBoxIcon.Exclamation);
                }
            }
        }

        private void cmbSubscriptions_SelectedValueChanged(object sender, EventArgs e)
        {
            txtSubscriptionName.Text = cmbSubscriptions.SelectedText;
        }

        private void btnSwitchSubscriptionInput_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            SwitchSubscriptionInputMethod();
        }

        void SwitchSubscriptionInputMethod()
        {
            switch (_subscriptionFormInputMethod)
            {
                case SubscriptionInputMethod.Picker:
                    SetSubscriptionInputMethod(SubscriptionInputMethod.Manual);
                    break;
                case SubscriptionInputMethod.Manual:
                    SetSubscriptionInputMethod(SubscriptionInputMethod.Picker);
                    break;
                default:
                    throw new NotSupportedException("No idea");
            }
        }

        void SetSubscriptionInputMethod(SubscriptionInputMethod method)
        {
            cmbSubscriptions.Visible = (method == SubscriptionInputMethod.Picker);
            btnRefreshSubs.Visible = (method == SubscriptionInputMethod.Picker);

            grpSubDetails.Visible = (method == SubscriptionInputMethod.Manual);

            switch (method)
            {
                case SubscriptionInputMethod.Picker:
                    btnSwitchSubscriptionInput.Text = "Enter manually";
                    break;
                case SubscriptionInputMethod.Manual:
                    btnSwitchSubscriptionInput.Text = "Select from list";
                    break;
                default:
                    throw new NotSupportedException("No idea what to do");
            }

            _subscriptionFormInputMethod = method;
        }

        enum SubscriptionInputMethod
        {
            Picker,
            Manual
        }

        private void btnRefreshSubs_Click(object sender, EventArgs e)
        {
            btnRefreshUISubs();
        }

        private void cmbEnvType_SelectedIndexChanged(object sender, EventArgs e)
        {
            RefreshPerformanceTierUI();
        }

        private void RefreshPerformanceTierUI()
        {
            grpProdTierDetails.Visible = cmbEnvType.SelectedItem?.ToString() == PERF_TIER_PROD;
            grpTestingTierDetails.Visible = cmbEnvType.SelectedItem?.ToString() != PERF_TIER_PROD;
        }

        private void lnkPlanLevelB1_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            OpenUrl("https://azure.microsoft.com/en-us/pricing/details/app-service/windows/");
        }

        private void lnkSQLLevelBasic_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            OpenUrl("https://azure.microsoft.com/en-us/pricing/details/azure-sql-database/single/");
        }

        private void lnkPlanLevelB2_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            OpenUrl("https://azure.microsoft.com/en-us/pricing/details/app-service/windows/");
        }

        private void lnkSQLLevelStandard_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            OpenUrl("https://azure.microsoft.com/en-us/pricing/details/azure-sql-database/single/");
        }

        void OpenUrl(string url)
        {
            System.Diagnostics.Process.Start(url);
        }

        private void AzureBaseConfigControl_Load(object sender, EventArgs e)
        {
            RefreshPerformanceTierUI();
        }
    }
}
