﻿namespace Tailspin.SurveyManagementService
{
    using System;
    using System.Collections.Generic;
    using System.Fabric;
    using System.Globalization;
    using System.Linq;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;
    using Microsoft.ServiceFabric.Services.Communication.Runtime;
    using Microsoft.ServiceFabric.Services.Remoting.Runtime;
    using Microsoft.ServiceFabric.Services.Runtime;
    using Tailspin.SurveyManagementService.Models;
    using Tailspin.SurveyManagementService.Client;
    using Tailspin.SurveyManagementService.Client.Models;

    /// <summary>
    /// An instance of this class is created for each service instance by the Service Fabric runtime.
    /// </summary>
    public class SurveyManagementService : StatelessService, ISurveyManagementService
    {
        private static string SurveyListPartitionKeyAndContainerName = "surveys";

        AzureTableFactory<SurveyInformationRow> _surveyInformationTableFactory;
        AzureBlobContainerFactory<Models.Survey> _surveyContainerFactory;

        public SurveyManagementService(StatelessServiceContext context,
                        AzureTableFactory<SurveyInformationRow> surveyInformationTableFactory,
                        AzureBlobContainerFactory<Models.Survey> surveyContainerFactory)
                        : base(context)
        {
            _surveyInformationTableFactory = surveyInformationTableFactory;
            _surveyContainerFactory = surveyContainerFactory;
        }

        public async Task<SurveyInformation> PublishSurveyAsync(Client.Models.Survey survey)
        {
            try
            {
                if (survey == null)
                {
                    throw new ArgumentNullException(nameof(survey));
                }

                if (string.IsNullOrEmpty(survey.SlugName) && string.IsNullOrEmpty(survey.Title))
                {
                    throw new ArgumentException($"{nameof(survey)} must have a slug or title");
                }
                var slugName = string.IsNullOrEmpty(survey.SlugName) ? GenerateSlug(survey.Title, 100) : survey.SlugName;
                survey.SlugName = slugName;
                survey.CreatedOn = DateTime.UtcNow;
                var table = _surveyInformationTableFactory(SurveyListPartitionKeyAndContainerName);
                var container = _surveyContainerFactory(SurveyListPartitionKeyAndContainerName);

                await table.EnsureExistsAsync();
                await container.EnsureExistsAsync();
                var row = survey.ToSurveyRow(SurveyListPartitionKeyAndContainerName);
                var surveyModel = survey.ToSurvey();

                var existingRows = await table.GetByStringPropertiesAsync(new[]
                {
                    new KeyValuePair<string, string>(nameof(SurveyInformationRow.PartitionKey), row.PartitionKey),
                    new KeyValuePair<string, string>(nameof(SurveyInformationRow.SlugName), row.SlugName)
                });

                if (existingRows.Count > 0)
                {
                    throw new InvalidOperationException(
                        $"Survey with PartitionKey: {row.PartitionKey} and SlugName: {row.SlugName} is already published");
                }

                // The survey is not published, so save to blob storage first, then table.
                await container.SaveAsync(surveyModel.SlugName, surveyModel);
                await table.AddAsync(row);
                return row.ToSurveyInformation();
            }
            catch (Exception ex)
            {
                ServiceEventSource.Current.ServiceRequestFailed(ex.ToString());
                throw new SurveyManagementServiceException();
            }
        }

        public async Task<ICollection<Client.Models.SurveyInformation>> ListSurveysAsync()
        {
            try
            {
                var table = _surveyInformationTableFactory(SurveyListPartitionKeyAndContainerName);
                await table.EnsureExistsAsync();

                var surveyRows = await table.GetByPartitionKeyAsync(SurveyListPartitionKeyAndContainerName)
                    .ConfigureAwait(false);
                return surveyRows
                    .Select(r => r.ToSurveyInformation())
                    .ToList();
            }
            catch (Exception ex)
            {
                ServiceEventSource.Current.ServiceRequestFailed(ex.ToString());
                throw new SurveyManagementServiceException();
            }
        }

        public async Task<Client.Models.Survey> GetSurveyAsync(string slugName)
        {
            if (string.IsNullOrWhiteSpace(slugName))
            {
                throw new ArgumentException($"Required {nameof(slugName)} parameter is empty.");
            }

            try
            {
                var container = _surveyContainerFactory(SurveyListPartitionKeyAndContainerName);
                var survey = await container.GetAsync(slugName);
                Client.Models.Survey result = null;
                if (survey != null)
                {
                    result = survey.ToSurvey();
                }

                return result;
            }
            catch (Exception ex)
            {
                ServiceEventSource.Current.ServiceRequestFailed(ex.ToString());
                throw new SurveyManagementServiceException();
            }
        }

        public async Task<ICollection<Client.Models.SurveyInformation>> GetLatestSurveysAsync()
        {
            try
            {
                var table = _surveyInformationTableFactory(SurveyListPartitionKeyAndContainerName);
                await table.EnsureExistsAsync();

                var surveyInformations = await table.GetLatestAsync(10)
                    .ConfigureAwait(false);
                return surveyInformations
                    .Select(r => r.ToSurveyInformation())
                    .ToList();
            }
            catch (Exception ex)
            {
                ServiceEventSource.Current.ServiceRequestFailed(ex.ToString());
                throw new SurveyManagementServiceException();
            }
        }

        /// <summary>
        /// Optional override to create listeners (e.g., TCP, HTTP) for this service replica to handle client or user requests.
        /// </summary>
        /// <returns>A collection of listeners.</returns>
        protected override IEnumerable<ServiceInstanceListener> CreateServiceInstanceListeners()
        {
            return new[] { new ServiceInstanceListener(context => this.CreateServiceRemotingListener(context)) };
        }

        private static string GenerateSlug(string txt, int maxLength)
        {
            string str = RemoveDiacritics(txt).ToLower();

            str = Regex.Replace(str, @"[^a-z0-9\s-]", string.Empty);
            str = Regex.Replace(str, @"\s+", " ").Trim();
            str = str.Substring(0, str.Length <= maxLength ? str.Length : maxLength).Trim();
            str = Regex.Replace(str, @"\s", "-");

            return str;
        }

        private static string RemoveDiacritics(string text)
        {
            return new string(text.Normalize(NormalizationForm.FormD)
                .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                .ToArray()).Normalize(NormalizationForm.FormC);
        }
    }
}
