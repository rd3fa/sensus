﻿// Copyright 2014 The Rector & Visitors of the University of Virginia
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using Sensus.Probes;
using Syncfusion.SfChart.XForms;
using Android.App;
using Android.Gms.Common.Apis;
using Android.Gms.Awareness;
using Android.Content;
using Android.Gms.Awareness.Fence;
using Sensus.Exceptions;
using Sensus.Probes.Movement;
using System.Collections.Generic;
using System.Threading;
using Newtonsoft.Json;
using Android.Gms.Common;
using Plugin.Permissions.Abstractions;
using System.Threading.Tasks;
using Plugin.Permissions;
using Android.Gms.Awareness.Snapshot;
using Sensus.UI.UiProperties;
using Sensus.Probes.Location;

namespace Sensus.Android.Probes.Movement
{
    public class AndroidActivityProbe : ListeningProbe
    {
        private enum ActivityPhase
        {
            Starting,
            During,
            Stopping
        };

        private enum FenceUpdateAction
        {
            Add,
            Remove
        }

        public const string AWARENESS_ID_BASE = "SENSUS_AWARENESS";
        public const string AWARENESS_ID_LOCATION = "LOCATION";

        private GoogleApiClient _awarenessApiClient;
        private Dictionary<string, AndroidActivityProbeBroadcastReceiver> _nameReciever;
        private int _locationChangeRadiusMeters;

        [EntryIntegerUiProperty("Location Change Radius (Meters):", true, 30)]
        public int LocationChangeRadiusMeters
        {
            get
            {
                return _locationChangeRadiusMeters;
            }
            set
            {
                if (value <= 0)
                {
                    value = 25;
                }

                _locationChangeRadiusMeters = value;
            }
        }

        [JsonIgnore]
        public override Type DatumType
        {
            get
            {
                return typeof(ActivityDatum);
            }
        }

        [JsonIgnore]
        public override string DisplayName
        {
            get
            {
                return "Activity";
            }
        }

        [JsonIgnore]
        protected override bool DefaultKeepDeviceAwake
        {
            get
            {
                return false;
            }
        }

        [JsonIgnore]
        protected override string DeviceAsleepWarning
        {
            get
            {
                return null;
            }
        }

        [JsonIgnore]
        protected override string DeviceAwakeWarning
        {
            get
            {
                return "This setting should not be enabled. It does not affect iOS and will unnecessarily reduce battery life on Android.";
            }
        }

        public AndroidActivityProbe()
        {
            _nameReciever = new Dictionary<string, AndroidActivityProbeBroadcastReceiver>();
            _locationChangeRadiusMeters = 25;

            CreateActivityPhaseReceivers(nameof(DetectedActivityFence.InVehicle));
            CreateActivityPhaseReceivers(nameof(DetectedActivityFence.OnBicycle));
            CreateActivityPhaseReceivers(nameof(DetectedActivityFence.OnFoot));
            CreateActivityPhaseReceivers(nameof(DetectedActivityFence.Running));
            CreateActivityPhaseReceivers(nameof(DetectedActivityFence.Still));
            CreateActivityPhaseReceivers(nameof(DetectedActivityFence.Tilting));
            CreateActivityPhaseReceivers(nameof(DetectedActivityFence.Unknown));
            CreateActivityPhaseReceivers(nameof(DetectedActivityFence.Walking));

            CreateReceiver(AWARENESS_ID_LOCATION).LocationChanged += (sender, e) =>
            {
                RequestLocationSnapshot();
            };
        }

        private void CreateActivityPhaseReceivers(string activity)
        {
            CreateReceiver(activity + "." + ActivityPhase.Starting).ActivityChanged += async (sender, activityDatum) =>
            {
                await StoreDatumAsync(activityDatum);
            };

            CreateReceiver(activity + "." + ActivityPhase.During).ActivityChanged += async (sender, activityDatum) =>
            {
                await StoreDatumAsync(activityDatum);
            };

            CreateReceiver(activity + "." + ActivityPhase.Stopping).ActivityChanged += async (sender, activityDatum) =>
            {
                await StoreDatumAsync(activityDatum);
            };
        }

        private AndroidActivityProbeBroadcastReceiver CreateReceiver(string name)
        {
            AndroidActivityProbeBroadcastReceiver receiver = new AndroidActivityProbeBroadcastReceiver();

            _nameReciever.Add(name, receiver);

            return receiver;
        }

        protected override void Initialize()
        {
            base.Initialize();

            // check for availability of Google Play Services
            int googlePlayServicesAvailability = GoogleApiAvailability.Instance.IsGooglePlayServicesAvailable(Application.Context);

            if (googlePlayServicesAvailability != ConnectionResult.Success)
            {
                string message = "Google Play Services are not available on this device.";

                if (googlePlayServicesAvailability == ConnectionResult.ServiceVersionUpdateRequired)
                {
                    message += " Please update your phone's Google Play Services app using the App Store. Then restart your study.";
                }

                message += " Email the study organizers and tell them you received the following error code:  " + googlePlayServicesAvailability;

                // the problem we've encountered is potentially fixable, so do not throw a NotSupportedException, as doing this would
                // disable the probe and prevent any future restart attempts from succeeding.
                throw new Exception(message);
            }

            // connect awareness client
            _awarenessApiClient = new GoogleApiClient.Builder(Application.Context).AddApi(Awareness.Api)

                .AddConnectionCallbacks(

                    bundle =>
                    {
                        SensusServiceHelper.Get().Logger.Log("Connected to Google Awareness API.", LoggingLevel.Normal, GetType());
                    },

                    status =>
                    {
                        SensusServiceHelper.Get().Logger.Log("Connection to Google Awareness API suspended. Status:  " + status, LoggingLevel.Normal, GetType());
                    })

                .Build();

            _awarenessApiClient.BlockingConnect();

            if (_awarenessApiClient.IsConnected)
            {
                // we need location permission in order to snapshot / fence the user's location.
                if (SensusServiceHelper.Get().ObtainPermission(Permission.Location) != PermissionStatus.Granted)
                {
                    string error = "Geolocation is not permitted on this device. Cannot start location fences.";
                    SensusServiceHelper.Get().FlashNotificationAsync(error);
                }
            }
            else
            {
                throw new Exception("Failed to connect with Google Awareness API.");
            }
        }

        protected override void StartListening()
        {
            AddPhasedFences(DetectedActivityFence.InVehicle, nameof(DetectedActivityFence.InVehicle));
            AddPhasedFences(DetectedActivityFence.OnBicycle, nameof(DetectedActivityFence.OnBicycle));
            AddPhasedFences(DetectedActivityFence.OnFoot, nameof(DetectedActivityFence.OnFoot));
            AddPhasedFences(DetectedActivityFence.Running, nameof(DetectedActivityFence.Running));
            AddPhasedFences(DetectedActivityFence.Still, nameof(DetectedActivityFence.Still));
            AddPhasedFences(DetectedActivityFence.Tilting, nameof(DetectedActivityFence.Tilting));
            AddPhasedFences(DetectedActivityFence.Unknown, nameof(DetectedActivityFence.Unknown));
            AddPhasedFences(DetectedActivityFence.Walking, nameof(DetectedActivityFence.Walking));

            RegisterReceiver(AWARENESS_ID_LOCATION);
            RequestLocationSnapshot();
        }

        private void AddPhasedFences(int activityId, string activityName)
        {
            RegisterReceiver(activityName + "." + ActivityPhase.Starting);
            RegisterReceiver(activityName + "." + ActivityPhase.During);
            RegisterReceiver(activityName + "." + ActivityPhase.Stopping);

            FenceUpdateRequestBuilder addFencesRequestBuilder = new FenceUpdateRequestBuilder();
            UpdateRequestBuilder(activityId, activityName, ActivityPhase.Starting, FenceUpdateAction.Add, ref addFencesRequestBuilder);
            UpdateRequestBuilder(activityId, activityName, ActivityPhase.During, FenceUpdateAction.Add, ref addFencesRequestBuilder);
            UpdateRequestBuilder(activityId, activityName, ActivityPhase.Stopping, FenceUpdateAction.Add, ref addFencesRequestBuilder);
            UpdateFences(addFencesRequestBuilder.Build());
        }

        private void RequestLocationSnapshot()
        {
            Task.Run(async () =>
            {
                if (await CrossPermissions.Current.CheckPermissionStatusAsync(Permission.Location) == PermissionStatus.Granted)
                {
                    // store current location
                    ILocationResult locationResult = await Awareness.SnapshotApi.GetLocationAsync(_awarenessApiClient);
                    global::Android.Locations.Location location = locationResult.Location;
                    DateTimeOffset timestamp = new DateTimeOffset(1970, 1, 1, 0, 0, 0, new TimeSpan()).AddMilliseconds(location.Time);
                    await StoreDatumAsync(new LocationDatum(timestamp, location.HasAccuracy ? location.Accuracy : -1, location.Latitude, location.Longitude));

                    // remove the previous location fence
                    FenceUpdateRequestBuilder requestBuilder = new FenceUpdateRequestBuilder();
                    UpdateRequestBuilder(null, AWARENESS_ID_BASE + "." + AWARENESS_ID_LOCATION, FenceUpdateAction.Remove, ref requestBuilder);
                    UpdateFences(requestBuilder.Build());

                    // add a fence around the current location
                    requestBuilder = new FenceUpdateRequestBuilder();
                    AwarenessFence locationFence = LocationFence.Exiting(location.Latitude, location.Longitude, _locationChangeRadiusMeters);
                    UpdateRequestBuilder(locationFence, AWARENESS_ID_BASE + "." + AWARENESS_ID_LOCATION, FenceUpdateAction.Add, ref requestBuilder);
                    UpdateFences(requestBuilder.Build());
                }

            }).Wait();
        }

        private void RegisterReceiver(string name)
        {
            Application.Context.RegisterReceiver(_nameReciever[name], new IntentFilter(AWARENESS_ID_BASE + "." + name));
        }

        protected override void StopListening()
        {
            RemovePhasedFences(DetectedActivityFence.InVehicle, nameof(DetectedActivityFence.InVehicle));
            RemovePhasedFences(DetectedActivityFence.OnBicycle, nameof(DetectedActivityFence.OnBicycle));
            RemovePhasedFences(DetectedActivityFence.OnFoot, nameof(DetectedActivityFence.OnFoot));
            RemovePhasedFences(DetectedActivityFence.Running, nameof(DetectedActivityFence.Running));
            RemovePhasedFences(DetectedActivityFence.Still, nameof(DetectedActivityFence.Still));
            RemovePhasedFences(DetectedActivityFence.Tilting, nameof(DetectedActivityFence.Tilting));
            RemovePhasedFences(DetectedActivityFence.Unknown, nameof(DetectedActivityFence.Unknown));
            RemovePhasedFences(DetectedActivityFence.Walking, nameof(DetectedActivityFence.Walking));

            // remove location fence
            FenceUpdateRequestBuilder requestBuilder = new FenceUpdateRequestBuilder();
            UpdateRequestBuilder(null, AWARENESS_ID_BASE + "." + AWARENESS_ID_LOCATION, FenceUpdateAction.Remove, ref requestBuilder);
            UpdateFences(requestBuilder.Build());
            UnregisterReceiver(AWARENESS_ID_LOCATION);

            // disconnect client
            _awarenessApiClient.Disconnect();
        }

        private void RemovePhasedFences(int activityId, string activityName)
        {
            try
            {
                FenceUpdateRequestBuilder removeFencesRequestBuilder = new FenceUpdateRequestBuilder();

                UpdateRequestBuilder(activityId, activityName, ActivityPhase.Starting, FenceUpdateAction.Remove, ref removeFencesRequestBuilder);
                UpdateRequestBuilder(activityId, activityName, ActivityPhase.During, FenceUpdateAction.Remove, ref removeFencesRequestBuilder);
                UpdateRequestBuilder(activityId, activityName, ActivityPhase.Stopping, FenceUpdateAction.Remove, ref removeFencesRequestBuilder);

                if (!UpdateFences(removeFencesRequestBuilder.Build()))
                {
                    // we'll catch this immediately
                    throw new Exception("Failed to remove fence (e.g., timed out).");
                }
            }
            catch (Exception ex)
            {
                SensusServiceHelper.Get().Logger.Log("Exception while removing fence:  " + ex, LoggingLevel.Normal, GetType());
            }

            // unconditionally unregister the receivers. we may have failed to remove the fence for a variety of reasons, but 
            // the caller wishes to discontinue updates from the fence.

            try
            {
                UnregisterReceiver(activityName + "." + ActivityPhase.Starting);
            }
            catch (Exception ex)
            {
                SensusServiceHelper.Get().Logger.Log("Exception while unregistering receiver:  " + ex, LoggingLevel.Normal, GetType());
            }

            try
            {
                UnregisterReceiver(activityName + "." + ActivityPhase.During);
            }
            catch (Exception ex)
            {
                SensusServiceHelper.Get().Logger.Log("Exception while unregistering receiver:  " + ex, LoggingLevel.Normal, GetType());
            }

            try
            {
                UnregisterReceiver(activityName + "." + ActivityPhase.Stopping);
            }
            catch (Exception ex)
            {
                SensusServiceHelper.Get().Logger.Log("Exception while unregistering receiver:  " + ex, LoggingLevel.Normal, GetType());
            }
        }

        private void UnregisterReceiver(string name)
        {
            Application.Context.UnregisterReceiver(_nameReciever[name]);
        }

        private void UpdateRequestBuilder(int activityId, string activityName, ActivityPhase phase, FenceUpdateAction action, ref FenceUpdateRequestBuilder requestBuilder)
        {
            AwarenessFence fence = null;

            if (phase == ActivityPhase.Starting)
            {
                fence = DetectedActivityFence.Starting(activityId);
            }
            else if (phase == ActivityPhase.During)
            {
                fence = DetectedActivityFence.During(activityId);
            }
            else if (phase == ActivityPhase.Stopping)
            {
                fence = DetectedActivityFence.Stopping(activityId);
            }
            else
            {
                SensusException.Report("Unknown activity phase:  " + phase);
                return;
            }
                
            UpdateRequestBuilder(fence, AWARENESS_ID_BASE + "." + activityName + "." + phase, action, ref requestBuilder);
        }

        private void UpdateRequestBuilder(AwarenessFence fence, string fenceId, FenceUpdateAction action, ref FenceUpdateRequestBuilder requestBuilder)
        {
            if (action == FenceUpdateAction.Add)
            {
                requestBuilder.AddFence(fenceId, fence, GetFencePendingIntent(fenceId));
            }
            else if (action == FenceUpdateAction.Remove)
            {
                requestBuilder.RemoveFence(fenceId);
            }
        }

        private PendingIntent GetFencePendingIntent(string fenceId)
        {
            Intent activityRecognitionCallbackIntent = new Intent(fenceId);
            return PendingIntent.GetBroadcast(Application.Context, 0, activityRecognitionCallbackIntent, PendingIntentFlags.UpdateCurrent);
        }

        private bool UpdateFences(IFenceUpdateRequest updateRequest)
        {
            ManualResetEvent updateWait = new ManualResetEvent(false);

            bool success = false;

            try
            {
                // update fences is asynchronous
                Awareness.FenceApi.UpdateFences(_awarenessApiClient, updateRequest).SetResultCallback<Statuses>(status =>
                {
                    try
                    {
                        if (status.IsSuccess)
                        {
                            SensusServiceHelper.Get().Logger.Log("Updated Google Awareness API fences.", LoggingLevel.Normal, GetType());
                            success = true;
                        }
                        else if (status.IsCanceled)
                        {
                            SensusServiceHelper.Get().Logger.Log("Google Awareness API fence update canceled.", LoggingLevel.Normal, GetType());
                        }
                        else if (status.IsInterrupted)
                        {
                            SensusServiceHelper.Get().Logger.Log("Google Awareness API fence update interrupted", LoggingLevel.Normal, GetType());
                        }
                        else
                        {
                            string message = "Unrecognized fence update status:  " + status;
                            SensusServiceHelper.Get().Logger.Log(message, LoggingLevel.Normal, GetType());
                            SensusException.Report(message);
                        }
                    }
                    catch (Exception ex)
                    {
                        SensusServiceHelper.Get().Logger.Log("Exception while processing update status:  " + ex, LoggingLevel.Normal, GetType());
                    }
                    finally
                    {
                        // ensure that wait is always set
                        updateWait.Set();
                    }
                });
            }
            // catch any errors from calling UpdateFences
            catch (Exception ex)
            {
                // ensure that wait is always set
                SensusServiceHelper.Get().Logger.Log("Exception while updating fences:  " + ex, LoggingLevel.Normal, GetType());
                updateWait.Set();
            }

            // we've seen cases where the update blocks indefinitely (e.g., due to outdated google play services on the phone). impose
            // a timeout to avoid such blocks.
            if (!updateWait.WaitOne(TimeSpan.FromSeconds(60)))
            {
                SensusServiceHelper.Get().Logger.Log("Timed out while updating fences.", LoggingLevel.Normal, GetType());
            }

            return success;
        }

        protected override ChartDataPoint GetChartDataPointFromDatum(Datum datum)
        {
            throw new NotImplementedException();
        }

        protected override ChartAxis GetChartPrimaryAxis()
        {
            throw new NotImplementedException();
        }

        protected override RangeAxisBase GetChartSecondaryAxis()
        {
            throw new NotImplementedException();
        }

        protected override ChartSeries GetChartSeries()
        {
            throw new NotImplementedException();
        }
    }
}