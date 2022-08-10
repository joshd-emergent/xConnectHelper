﻿using Sitecore.Configuration;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

namespace Sitecore.SharedSource.XConnectHelper.Impl
{
    public class XConnectValidator
    {
        public string ServiceName { get; set; }
        protected readonly string ConnectionStringName;
        public IList<string> Messages = new List<string>();
        public bool Error = false;

        public XConnectValidator(string serviceName, string connectionStringName)
        {
            ServiceName = serviceName;
            ConnectionStringName = connectionStringName;
        }

        public void Validate()
        {
            this.ValidateConnectionString();
            if (Error) return;
            this.ValidateClientCertificate();
            if (Error) return;
            this.ValidateHttpConnection();
            if (Error) return;
        }

        protected void ValidateConnectionString()
        {
            var connString = ConfigurationManager.ConnectionStrings[ConnectionStringName]?.ConnectionString;

            if (string.IsNullOrEmpty(connString))
            {
                Messages.Add($"No '{ConnectionStringName}' connection string configured.");
                Error = true;
                return;
            }

            connString = ConfigurationManager.ConnectionStrings[$"{ConnectionStringName}.certificate"]?.ConnectionString;

            if (string.IsNullOrEmpty(connString))
            {
                Messages.Add($"No '{ConnectionStringName}.certificate' connection string configured.");
                Error = true;
                return;
            }
        }

        protected void ValidateClientCertificate()
        {
            var connString = ConfigurationManager.ConnectionStrings[$"{ConnectionStringName}.certificate"].ConnectionString;

            Dictionary<string, string> connStringParts = connString.Split(';')
                .Select(t => t.Split(new char[] { '=' }, 2))
                .ToDictionary(t => t[0].Trim(), t => t[1].Trim(), StringComparer.InvariantCultureIgnoreCase);

            var storeLocation = StoreLocation.LocalMachine;
            Enum.TryParse(connStringParts["StoreLocation"], out storeLocation);

            var findType = X509FindType.FindByThumbprint;
            Enum.TryParse(connStringParts["FindType"], out findType);

            var certStore = new X509Store(connStringParts["StoreName"], storeLocation);
            certStore.Open(OpenFlags.ReadOnly);

            // Include invalid certifiates first
            var certCollection = certStore.Certificates.Find(findType, connStringParts["FindValue"], false);

            certStore.Close();

            if (certCollection.Count == 0)
            {
                Messages.Add($"Client certificate not found. Connection String: '{connString}'");

                if (!Regex.IsMatch(connStringParts["FindValue"], "^[A-F0-9]+$"))
                {
                    Messages.Add($"Thumbprint not uppercase or it contains invalid characters: '{connStringParts["FindValue"]}'");
                }

                Error = true;
                return;
            }
            else if (certCollection.Count > 1)
            {
                Messages.Add($"More than one client certificates found. Connection String: '{connString}'");
                Error = true;
                return;
            }

            // Now only valid certificates
            certStore.Open(OpenFlags.ReadOnly);
            certCollection = certStore.Certificates.Find(X509FindType.FindByThumbprint, connStringParts["FindValue"], false);
            certStore.Close();


            if (certCollection.Count == 0)
            {
                Messages.Add($"Client certificate is invalid. Check its root certificate. Connection String: '{connString}'");
                Error = true;
                return;
            }
            else
            {
                //if (!certCollection[0].Verify())
                //{

                //    Messages.Add($"Joshd - Client certificate is invalid. Check its root certificate. Connection String: '{connString}' {certCollection[0].IssuerName.Name}");
                //    Error = true;
                //    return;
                //}
            }

            // Warn if expiration date is within range
            try
            {
                DateTime? validUntil = certCollection[0]?.NotAfter;
                if (validUntil.HasValue)
                {
                    if (validUntil.Value < DateTime.Now.AddDays(Settings.GetIntSetting("xConnectHelper.WarnDaysBeforeCertExpiration", 90)))
                    {
                        Messages.Add($"WARN: Client certificate expires on: {validUntil.Value.ToShortDateString()}");
                    }
                }
            }
            catch (Exception)
            {

            }

            // Check private key access            
            //try
            //{
            //    var privateKey = certCollection[0]?.PrivateKey;
            //}
            //catch (CryptographicException)
            //{
            //    Messages.Add($"Client certificate was found but private key is not accessible by this application pool. Set rights in cert store. Connection String: '{connString}'");
            //    Error = true;
            //    return;
            //}
        }

        public void ValidateHttpConnection()
        {
            var connString = ConfigurationManager.ConnectionStrings[ConnectionStringName]?.ConnectionString;
            var certConnString = ConfigurationManager.ConnectionStrings[$"{ConnectionStringName}.certificate"].ConnectionString;
            var originalValidationCallback = ServicePointManager.ServerCertificateValidationCallback;

            try
            {
                var request = WebRequest.CreateHttp(connString);

                Dictionary<string, string> certConnStringParts = certConnString.Split(';')
                    .Select(t => t.Split(new char[] { '=' }, 2))
                    .ToDictionary(t => t[0].Trim(), t => t[1].Trim(), StringComparer.InvariantCultureIgnoreCase);

                var storeLocation = StoreLocation.LocalMachine;
                Enum.TryParse(certConnStringParts["StoreLocation"], out storeLocation);

                var findType = X509FindType.FindByThumbprint;
                Enum.TryParse(certConnStringParts["FindType"], out findType);

                var certStore = new X509Store(certConnStringParts["StoreName"], storeLocation);
                certStore.Open(OpenFlags.ReadOnly);
                var cert = certStore.Certificates.Find(X509FindType.FindByThumbprint, certConnStringParts["FindValue"], false)[0];
                certStore.Close();

                request.ClientCertificates.Add(cert);
                // Enforce SSL validation
                ServicePointManager.ServerCertificateValidationCallback += new RemoteCertificateValidationCallback(ValidateServerCertificate);

                HttpWebResponse response = (HttpWebResponse)request.GetResponse();
                Stream dataStream = response.GetResponseStream();
                StreamReader reader = new StreamReader(dataStream);
                string responseFromServer = reader.ReadToEnd();

                return;
            }
            catch (WebException)
            {
            }
            finally
            {
                ServicePointManager.ServerCertificateValidationCallback = originalValidationCallback;
            }

            try
            {
                WebRequest request = WebRequest.Create(connString);

                // Ignore SSL validation
                ServicePointManager.ServerCertificateValidationCallback += new RemoteCertificateValidationCallback(IgnoreServerCertificate);

                HttpWebResponse response = (HttpWebResponse)request.GetResponse();
                Stream dataStream = response.GetResponseStream();
                StreamReader reader = new StreamReader(dataStream);
                string responseFromServer = reader.ReadToEnd();

                Messages.Add($"'{connString}' is reachable but has an invalid server certificate.");
                return;
            }
            catch (WebException ex)
            {
                Messages.Add($"Unable to establish a connection to: '{connString}' Error: {ex.Message}");
                Error = true;
            }
            finally
            {
                ServicePointManager.ServerCertificateValidationCallback = originalValidationCallback;
            }
        }

        private static bool ValidateServerCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
        {
            return sslPolicyErrors == SslPolicyErrors.None;
        }

        private static bool IgnoreServerCertificate(object sender, X509Certificate certificate, X509Chain chain, SslPolicyErrors sslPolicyErrors)
        {
            return true;
        }
    }
}