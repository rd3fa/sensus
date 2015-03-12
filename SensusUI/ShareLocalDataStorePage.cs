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
using Xamarin.Forms;
using System.Threading;
using SensusService.DataStores.Local;
using SensusService;
using System.IO;
using System.Collections.Generic;

namespace SensusUI
{
    public class ShareLocalDataStorePage : ContentPage
    {
        private bool _cancel;

        public ShareLocalDataStorePage(LocalDataStore localDataStore)
        {
            _cancel = false;

            Title = "Sharing Local Data Store";

            StackLayout contentLayout = new StackLayout
            {
                Orientation = StackOrientation.Vertical,
                VerticalOptions = LayoutOptions.FillAndExpand
            };
            
            ProgressBar progressBar = new ProgressBar
            {
                Progress = 0,
                HorizontalOptions = LayoutOptions.FillAndExpand
            };

            contentLayout.Children.Add(progressBar);

            Label statusLabel = new Label
            {
                FontSize = 20,
                HorizontalOptions = LayoutOptions.FillAndExpand
            };

            contentLayout.Children.Add(statusLabel);

            Button cancelButton = new Button
            {
                Text = "Cancel",
                FontSize = 20,
                HorizontalOptions = LayoutOptions.FillAndExpand
            };
                        
            cancelButton.Clicked += async (o, e) =>
            {
                await Navigation.PopAsync();
            };
                                      
            new Thread(async () =>
                {                    
                    string sharePath = UiBoundSensusServiceHelper.Get(true).GetSharePath(".json");
                    bool errorWritingShareFile = false;
                    try
                    {              
                        Device.BeginInvokeOnMainThread(() => statusLabel.Text = "Gathering data...");
                        List<Datum> localData = localDataStore.GetDataForRemoteDataStore(progress => Device.BeginInvokeOnMainThread(() => progressBar.ProgressTo(progress, 250, Easing.Linear)), () => _cancel);

                        Device.BeginInvokeOnMainThread(() => 
                            {
                                progressBar.ProgressTo(0, 0, Easing.Linear);
                                statusLabel.Text = "Writing data to file...";
                            });
                        
                        using(StreamWriter shareFile = new StreamWriter(sharePath))
                        {
                            int dataWritten = 0;
                            foreach (Datum datum in localData)
                            {
                                shareFile.WriteLine(datum.GetJSON(null));

                                if((++dataWritten % (localData.Count / 10)) == 0)
                                    Device.BeginInvokeOnMainThread(() => progressBar.ProgressTo(dataWritten / (double)localData.Count, 250, Easing.Linear));
                            }

                            shareFile.Close();
                        }
                    }
                    catch (Exception ex)
                    {
                        errorWritingShareFile = true;
                        string message = "Error writing share file:  " + ex.Message;
                        SensusServiceHelper.Get().FlashNotificationAsync(message);
                        SensusServiceHelper.Get().Logger.Log(message, LoggingLevel.Normal, GetType());
                        await Navigation.PopAsync();
                    }

                    if (!_cancel && !errorWritingShareFile)
                    {
                        Device.BeginInvokeOnMainThread(async () => await Navigation.PopAsync());
                        SensusServiceHelper.Get().ShareFileAsync(sharePath, "Sensus Data");
                    }

                }).Start();

            Content = new ScrollView
            { 
                Content = contentLayout
            };
        }

        protected override void OnDisappearing()
        {
            base.OnDisappearing();

            _cancel = true;
        }
    }
}

