﻿using System;
using BrightstarDB.Storage.Persistence;
#if BTREESTORE
using BrightstarDB.Storage.BTreeStore;
#endif

namespace BrightstarDB.Storage
{
    internal static class StoreManagerFactory
    {
        public static IStoreManager GetStoreManager(StoreConfiguration storeConfiguration = null)
        {
            if (storeConfiguration == null)
            {
                storeConfiguration = StoreConfiguration.DefaultStoreConfiguration;
            }
            if (storeConfiguration.StoreManagerType != null)
            {
                return
                    Activator.CreateInstance(storeConfiguration.StoreManagerType, storeConfiguration) as IStoreManager;
            }
#if BTREESTORE
#if SILVERLIGHT
            return new IsolatedStorageStoreManager(storeConfiguration);
#else
            return storeConfiguration.UseIsolatedStorage ? (IStoreManager) new IsolatedStorageStoreManager(storeConfiguration) : new FileStoreManager(storeConfiguration);
#endif
#else
#if WINDOWS_PHONE
            return  new BPlusTreeStore.BPlusTreeStoreManager(storeConfiguration, new IsolatedStoragePersistanceManager());
#else
            return storeConfiguration.UseIsolatedStorage ? new BPlusTreeStore.BPlusTreeStoreManager(storeConfiguration, new IsolatedStoragePersistanceManager()) : new BPlusTreeStore.BPlusTreeStoreManager(storeConfiguration, new FilePersistenceManager());
#endif
#endif


        }
    }
}
