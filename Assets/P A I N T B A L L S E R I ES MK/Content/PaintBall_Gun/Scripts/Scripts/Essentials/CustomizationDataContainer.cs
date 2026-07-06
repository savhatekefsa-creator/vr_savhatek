using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[CreateAssetMenu(menuName = "Customization Data Container")]
public class CustomizationDataContainer : ScriptableObject
{
    public List<CustomizationGroupDataStructure> CustomizationGroupsData = new List<CustomizationGroupDataStructure>();
}