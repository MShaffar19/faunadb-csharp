﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FaunaDB.Collections;
using FaunaDB.Errors;
using FaunaDB.Query;
using FaunaDB.Types;
using Newtonsoft.Json;

namespace FaunaDB.Client
{
    /// <summary>
    /// Directly communicates with FaunaDB via JSON.
    /// </summary>
    public class Client
    {
        readonly IClientIO clientIO;

        /// <param name="domain">Base URL for the FaunaDB server.</param>
        /// <param name="scheme">Scheme of the FaunaDB server. Should be "http" or "https".</param>
        /// <param name="port">Port of the FaunaDB server.</param>
        /// <param name="timeout">Timeout. Defaults to 1 minute.</param>
        /// <param name="secret">Auth token for the FaunaDB server.</param>
        /// <param name="clientIO">Optional IInnerClient. Used only for testing.</param>"> 
        public Client(
            string domain = "rest.faunadb.com",
            string scheme = "https",
            int? port = null,
            TimeSpan? timeout = null,
            string secret = null,
            IClientIO clientIO = null)
        {
            if (port == null)
                port = scheme == "https" ? 443 : 80;

            this.clientIO = clientIO ??
                new DefaultClientIO(new Uri(scheme + "://" + domain + ":" + port), timeout ?? TimeSpan.FromSeconds(60), secret);
        }

        internal Client(IClientIO root)
        {
            clientIO = root;
        }

        public Client NewSessionClient(string secret) =>
            new Client(clientIO.NewSessionClient(secret));

        /// <summary>
        /// Use the FaunaDB query API.
        /// </summary>
        /// <param name="expression">Expression generated by methods of <see cref="Query"/>.</param>
        public async Task<Value> Query(Expr expression) =>
            await Execute(HttpMethodKind.Post, "", expression).ConfigureAwait(false);

        public async Task<Value[]> Query(params Expr[] expressions)
        {
            var response = await Query(UnescapedArray.Of(expressions)).ConfigureAwait(false);
            return response.Collect(Field.Root).ToArray();
        }

        public async Task<IEnumerable<Value>> Query(IEnumerable<Expr> expressions)
        {
            var response = await Query(UnescapedArray.Of(expressions)).ConfigureAwait(false);
            return response.Collect(Field.Root);
        }

        /// <summary>
        /// Ping FaunaDB.
        /// See the <see href="https://faunadb.com/documentation/rest#other">docs</see>. 
        /// </summary>
        public async Task<string> Ping(string scope = null, int? timeout = null) =>
            (string)await Execute(HttpMethodKind.Get, "ping", query: ImmutableDictionary.Of("scope", scope, "timeout", timeout?.ToString())).ConfigureAwait(false);

        async Task<Value> Execute(HttpMethodKind action, string path, Expr data = null, IReadOnlyDictionary<string, string> query = null)
        {
            var dataString = data == null ?  null : JsonConvert.SerializeObject(data, Formatting.None);
            var responseHttp = await clientIO.DoRequest(action, path, dataString, query).ConfigureAwait(false);

            RaiseForStatusCode(responseHttp);

            var responseContent = FromJson(responseHttp.ResponseContent);
            return responseContent["resource"];
        }

        internal struct ErrorsWrapper
        {
            public IReadOnlyList<QueryError> Errors;
        }

        internal static void RaiseForStatusCode(RequestResult resultRequest)
        {
            var statusCode = resultRequest.StatusCode;

            if (statusCode >= 200 && statusCode < 300)
                return;

            var wrapper = JsonConvert.DeserializeObject<ErrorsWrapper>(resultRequest.ResponseContent);

            var response = new QueryErrorResponse(statusCode, wrapper.Errors);

            switch (statusCode)
            {
                case 400:
                    throw new BadRequest(response);
                case 401:
                    throw new Unauthorized(response);
                case 403:
                    throw new PermissionDenied(response);
                case 404:
                    throw new NotFound(response);
                case 405:
                    throw new MethodNotAllowed(response);
                case 500:
                    throw new InternalError(response);
                case 503:
                    throw new UnavailableError(response);
                default:
                    throw new UnknowException(response);
            }
        }

        static ObjectV FromJson(string json)
        {
            try
            {
                var settings = new JsonSerializerSettings { DateParseHandling = DateParseHandling.None };
                return JsonConvert.DeserializeObject<ObjectV>(json, settings);
            }
            catch (JsonReaderException ex)
            {
                throw new InvalidResponseException($"Bad JSON: {ex}");
            }
        }
    }
}
