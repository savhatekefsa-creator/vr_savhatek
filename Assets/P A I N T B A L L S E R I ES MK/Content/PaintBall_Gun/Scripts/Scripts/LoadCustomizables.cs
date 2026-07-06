using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class LoadCustomizables : MonoBehaviour
{
    public CustomizationDataContainer LoadFrom;
    public CustomizableGroup[] GroupsToLoadID;

    private void Start()
    {
        GroupsToLoadID = GetComponentsInChildren<CustomizableGroup>();
        Invoke("Save", 0.02f);
    }
    public void Save()
    {
        Debug.Log("Trying Load Customization Data");
        if (LoadFrom.CustomizationGroupsData.Count != GroupsToLoadID.Length) return;
        LoadCustomizableGroups(GroupsToLoadID, LoadFrom);
    }
    public static void LoadCustomizableGroups(CustomizableGroup[] groupList, CustomizationDataContainer dataContainer)
    {
        //Load Data
        for (int i = 0; i < groupList.Length; i++)
        {
            if (dataContainer.CustomizationGroupsData[i].GroupName == groupList[i].name)
            {
                groupList[i].ItemID = dataContainer.CustomizationGroupsData[i].CurrentGroupItemID;
                groupList[i].UpdateVisibility();
            }
        }
        Debug.Log("The customization data was loaded successfully");
    }
}
