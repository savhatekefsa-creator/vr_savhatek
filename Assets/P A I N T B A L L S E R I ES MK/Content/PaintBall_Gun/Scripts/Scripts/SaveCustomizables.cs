using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class SaveCustomizables : MonoBehaviour
{
    public CustomizationDataContainer SaveOn;
    public CustomizableGroup[] GroupsToSaveID;

    private void Start()
    {
        GroupsToSaveID = GetComponentsInChildren<CustomizableGroup>();
    }
    public void Save()
    {
        Debug.Log("Trying Save Customization Data");
        SaveCustomizableGroups(GroupsToSaveID, SaveOn);
    }
    public static void SaveCustomizableGroups(CustomizableGroup[] groupList, CustomizationDataContainer dataContainer)
    {
        //Clear Current Data
        dataContainer.CustomizationGroupsData.Clear();

        //Write Data
        foreach (CustomizableGroup group in groupList)
        {
            CustomizationGroupDataStructure dataToWrite = new CustomizationGroupDataStructure();
            dataToWrite.CurrentGroupItemID = group.ItemID;
            dataToWrite.GroupName = group.gameObject.name;

            dataContainer.CustomizationGroupsData.Add(dataToWrite);
        }
        Debug.Log("The customization data was saved successfully");
    }
}
