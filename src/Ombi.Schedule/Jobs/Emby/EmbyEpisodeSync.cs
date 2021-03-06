﻿#region Copyright
// /************************************************************************
//    Copyright (c) 2017 Jamie Rees
//    File: EmbyEpisodeCacher.cs
//    Created By: Jamie Rees
//   
//    Permission is hereby granted, free of charge, to any person obtaining
//    a copy of this software and associated documentation files (the
//    "Software"), to deal in the Software without restriction, including
//    without limitation the rights to use, copy, modify, merge, publish,
//    distribute, sublicense, and/or sell copies of the Software, and to
//    permit persons to whom the Software is furnished to do so, subject to
//    the following conditions:
//   
//    The above copyright notice and this permission notice shall be
//    included in all copies or substantial portions of the Software.
//   
//    THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
//    EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
//    MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
//    NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
//    LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
//    OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
//    WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//  ************************************************************************/
#endregion

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Hangfire;
using Microsoft.Extensions.Logging;
using Ombi.Api.Emby;
using Ombi.Core.Settings;
using Ombi.Core.Settings.Models.External;
using Ombi.Store.Entities;
using Ombi.Store.Repository;

namespace Ombi.Schedule.Jobs.Emby
{
    public class EmbyEpisodeSync : IEmbyEpisodeSync
    {
        public EmbyEpisodeSync(ISettingsService<EmbySettings> s, IEmbyApi api, ILogger<EmbyEpisodeSync> l, IEmbyContentRepository repo,
            IEmbyAvaliabilityChecker checker)
        {
            _api = api;
            _logger = l;
            _settings = s;
            _repo = repo;
            _avaliabilityChecker = checker;
            _settings.ClearCache();
        }

        private readonly ISettingsService<EmbySettings> _settings;
        private readonly IEmbyApi _api;
        private readonly ILogger<EmbyEpisodeSync> _logger;
        private readonly IEmbyContentRepository _repo;
        private readonly IEmbyAvaliabilityChecker _avaliabilityChecker;


        public async Task Start()
        {
            var settings = await _settings.GetSettingsAsync();

            foreach (var server in settings.Servers)
            {
                await CacheEpisodes(server);
            }

            BackgroundJob.Enqueue(() => _avaliabilityChecker.Start());
        }

        private async Task CacheEpisodes(EmbyServers server)
        {
            var allEpisodes = await _api.GetAllEpisodes(server.ApiKey, server.AdministratorId, server.FullUri);
            var epToAdd = new List<EmbyEpisode>();

            foreach (var ep in allEpisodes.Items)
            {
                if (ep.LocationType.Equals("Virtual", StringComparison.CurrentCultureIgnoreCase))
                {
                    // This means that we don't actully have the file, it's just Emby being nice and showing future stuff
                    continue;
                }

                var epInfo = await _api.GetEpisodeInformation(ep.Id, server.ApiKey, server.AdministratorId, server.FullUri);
                if (epInfo?.ProviderIds?.Tvdb == null)
                {
                    continue;
                }

                // Let's make sure we have the parent request, stop those pesky forign key errors,
                // Damn me having data integrity
                var parent = await _repo.GetByEmbyId(epInfo.SeriesId);
                if (parent == null)
                {
                    _logger.LogInformation("The episode {0} does not relate to a series, so we cannot save this", ep.Name);
                    continue;
                }

                var existingEpisode = await _repo.GetByEmbyId(ep.Id);
                if (existingEpisode == null)
                {
                    // add it
                    epToAdd.Add(new EmbyEpisode
                    {
                        EmbyId = ep.Id,
                        EpisodeNumber = ep.IndexNumber,
                        SeasonNumber = ep.ParentIndexNumber,
                        ParentId = ep.SeriesId,
                        ProviderId = epInfo.ProviderIds.Tvdb,
                        Title = ep.Name,
                        AddedAt = DateTime.UtcNow
                    });
                }
            }

            if (epToAdd.Any())
            {
                await _repo.AddRange(epToAdd);
            }
        }

        private bool _disposed;
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                _settings?.Dispose();
                _repo?.Dispose();
                _avaliabilityChecker?.Dispose();
            }
            _disposed = true;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}