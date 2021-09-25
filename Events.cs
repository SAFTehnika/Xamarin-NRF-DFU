// Copyright (c) 2018 SAF Tehnika. All rights reserved. See License.txt in the project root for license information.

using System;

namespace Plugin.XamarinNordicDFU
{
    public static class DFUEvents
    {
        public delegate void OnSuccessDelegate(TimeSpan timeElapsed);
        /// <summary>
        /// Firmware is successfully transfered
        /// </summary>
        public static OnSuccessDelegate OnSuccess = null;

        public delegate void OnProgressChangedDelegate(float percentage, TimeSpan timeElapsed);
        /// <summary>
        /// Firmware Upload progress changed
        /// </summary>
        public static OnProgressChangedDelegate OnFimwareProgressChanged = null;

        public delegate void OnErrorDelegate(string message);
        /// <summary>
        /// Error that caused transfer to abort, after receiving error transfer is aborted. This may not be the root cause. Check OnExtendedError and OnResponseError events
        /// </summary>
        public static OnErrorDelegate OnError = null;

        public delegate void OnExtendedErrorsDelegate(DFUOperationExtendedErrorCode error);
        /// <summary>
        /// Extended error in transfer
        /// </summary>
        public static OnExtendedErrorsDelegate OnExtendedError = null;

        public delegate void OnResponseErrorsDelegate(DFUOperationResultCode error);
        /// <summary>
        /// Extended error in transfer
        /// </summary>
        public static OnResponseErrorsDelegate OnResponseError = null;

        public delegate void OnLogMessageDelegate(string message);
        /// <summary>
        /// DFU process logging
        /// </summary>
        public static OnLogMessageDelegate OnLogMessage = null;
    }
}
