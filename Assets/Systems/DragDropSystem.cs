using FYFY;
using FYFY_plugins.PointerManager;
using System;
using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

/// Ce syst�me permet la gestion du drag and drop des diff�rents �l�ments de construction de la s�quence d'action.
/// Il g�re entre autre :
///		Le drag and drop d'un �l�ment du panel librairie vers une s�quence d'action
///		Le drag and drop d'un �l�ment d'une s�quence d'action vers une s�quence d'action (la m�me ou autre)
///		Le drag and drop d'un �l�ment (biblioth�que ou sequence d'action) vers l'ext�rieur (pour le supprimer)
///		Le clic droit sur un �l�ment dans une sequence d'action pour le supprimer
///		Le double click sur un �l�ment de la biblioth�que pour l'ajouter sur la derni�re dropzone utilis�e
/// 
/// <summary>
/// beginDragElementFromLibrary
///		Pour le d�but du drag and drop d'un �l�ment venant de la biblioth�que
/// beginDragElementFromEditableScript
///		Pour le d�but du drag and drop d'un �l�ment venant de la s�quence d'action en construction
/// dragElement
///		Pendant le drag d'un �l�ment
/// endDragElement
///		A la fin d'un drag and drop si l'�l�ment n'est pas l�ch� dans un container pour la cr�ation d'une s�quence
/// deleteElement
///		Destruction d'une block d'action
/// </summary>

public class DragDropSystem : FSystem
{
	// Les familles
    private Family f_viewportContainerPointed = FamilyManager.getFamily(new AllOfComponents(typeof(PointerOver), typeof(ViewportContainer))); // Les container contenant les containers �ditables
	private Family f_dropZone = FamilyManager.getFamily(new AllOfComponents(typeof(DropZone))); // Les drops zones
	private Family f_dropZoneEnabled = FamilyManager.getFamily(new AllOfComponents(typeof(DropZone)), new AnyOfProperties(PropertyMatcher.PROPERTY.ACTIVE_IN_HIERARCHY)); // Les drops zones visibles
	private Family f_dropArea = FamilyManager.getFamily(new AnyOfComponents(typeof(DropZone), typeof(ReplacementSlot))); // Les drops zones et les replacement slots
	private Family f_operators = FamilyManager.getFamily(new AllOfComponents(typeof(BaseOperator)));
	private Family f_elementToDelete = FamilyManager.getFamily(new AllOfComponents(typeof(NeedToDelete)));
	private Family f_elementToRefresh = FamilyManager.getFamily(new AllOfComponents(typeof(NeedRefreshHierarchy)));
	private Family f_defaultDropZone = FamilyManager.getFamily(new AllOfComponents(typeof(Selected)));

	private Family f_playMode = FamilyManager.getFamily(new AllOfComponents(typeof(PlayMode)));
	private Family f_editMode = FamilyManager.getFamily(new AllOfComponents(typeof(EditMode)));

	private Family f_replacementSlot = FamilyManager.getFamily(new AllOfComponents(typeof(Outline), typeof(ReplacementSlot)), new AnyOfProperties(PropertyMatcher.PROPERTY.ACTIVE_IN_HIERARCHY));

	// Les variables
	private GameData gameData;
	private GameObject itemDragged; // L'item (ici bloc d'action) en cours de drag
	private Coroutine viewLastDropZone = null;
	public GameObject mainCanvas; // Le canvas principal
	public GameObject lastDropZoneUsed; // La derni�re dropzone utilis�e
	public AudioSource audioSource; // Pour le son d'ajout de bloc
	//Pour la gestion du double clic
	private float lastClickTime;
	public float catchTime;
	public RectTransform editableContainers;

	// L'instance
	public static DragDropSystem instance;

	public DragDropSystem()
    {
		instance = this;
	}

    protected override void onStart()
	{
		GameObject go = GameObject.Find("GameData");
		if (go != null)
			gameData = go.GetComponent<GameData>();

		f_elementToDelete.addEntryCallback(deleteElement);
		f_defaultDropZone.addEntryCallback(selectNewDefaultDropZone);
		f_elementToRefresh.addEntryCallback(delegate (GameObject go)
		{
			refreshHierarchyContainers(go);
			foreach (NeedRefreshHierarchy ntr in go.GetComponents<NeedRefreshHierarchy>())
				GameObjectManager.removeComponent(ntr);
		});
		f_playMode.addEntryCallback(delegate {
			Pause = true;
			string scriptsContent = "";
			foreach (Transform viewportScriptContainer in editableContainers)
				scriptsContent += exportScriptToString(viewportScriptContainer.Find("ScriptContainer"), null);
			GameObjectManager.addComponent<ActionPerformedForLRS>(MainLoop.instance.gameObject, new
			{
				verb = "executed",
				objectType = "program",
				activityExtensions = new Dictionary<string, string>() {
				{ "content", scriptsContent }
			}
			});
		});
		f_editMode.addEntryCallback(delegate {
			Pause = false;
		});
	}

	// toggle toutes les dropzones
	private void setDropZoneState(bool value)
	{
		foreach (GameObject Dp in f_dropZone)
		{
			GameObjectManager.setGameObjectState(Dp.transform.gameObject, value);
			Dp.transform.GetChild(0).gameObject.SetActive(false); // Be sure the drop zone is disabled
		}

		// enable eventManager of each operator
		foreach (GameObject op in f_operators)
        {
			// be sure Outline is disabled on all operator
			Transform eventManager = op.transform.Find("EventManager");
			if (eventManager != null)
			{
				if (op != itemDragged)
					eventManager.gameObject.SetActive(value);
				else
					eventManager.gameObject.SetActive(false); // means object dragged is this => always disable eventManager
			}
        }
	}

	// used by prefabs (Captors, boolean operators and drop areas)
	public void checkHighlightDropArea(GameObject dropArea)
    {
		if (itemDragged != null) {
			// First case => the dropArea is a drop zone and item dragged is a base element, we enable child red bar of the drop zone
			if (dropArea.GetComponent<DropZone>() && itemDragged.GetComponent<BaseElement>())
				GameObjectManager.setGameObjectState(dropArea.transform.GetChild(0).gameObject, true);
			else
			{ // Second case => the drop area is a replacement slot, we have to manage base element and condition element
				ReplacementSlot repSlot = dropArea.GetComponent<ReplacementSlot>();
				// Check if the replacement area and the item dragged are the same type
				if (repSlot && ((repSlot.slotType == ReplacementSlot.SlotType.BaseElement && itemDragged.GetComponent<BaseElement>()) ||
								(repSlot.slotType == ReplacementSlot.SlotType.BaseCondition && itemDragged.GetComponent<BaseCondition>())))
				{
					// Be sure to not outline the drop area before looking for enabled outline in parents and childs
					repSlot.GetComponent<Outline>().enabled = false;
					// First remove outline from parents, indeed with nested conditions all the hierarchy is outlined
					foreach (Outline outline in repSlot.GetComponentsInParent<Outline>(true))
						outline.enabled = false;
					// check if a child is not already outlined
					foreach (Outline outline in repSlot.GetComponentsInChildren<Outline>(true))
						if (outline.enabled)
							return;
					// no child is already outline => then we can outline this drop area
					repSlot.GetComponent<Outline>().enabled = true;
				}
			}
		}
    }

	// used by prefabs on ReplacementSlot
	public void unhighlightDropArea(GameObject dropArea)
	{
		if (itemDragged != null && itemDragged.GetComponent<BaseCondition>() && !dropArea.transform.IsChildOf(itemDragged.transform))
		{
			Outline[] outlines = dropArea.GetComponentsInParent<Outline>(); // the first is this
			
			// if we found an outline in parent, we enable the first parent that is the second item in the list (the first is the outline of the current drop area)
			if (outlines.Length >= 2)
				outlines[1].enabled = true;
			// then disable outline of current dropArea
			dropArea.GetComponent<Outline>().enabled = false;
		}
	}

	// Lors de la selection (d�but d'un drag) d'un bloc de la librairie (voir inspector)
	public void beginDragElementFromLibrary(BaseEventData element)
    {
		// On verifie si c'est un �v�nement g�n�r� par le bouton gauche de la souris
		if (!Pause && gameData.dragDropEnabled && (element as PointerEventData).button == PointerEventData.InputButton.Left && element.selectedObject != null)
		{
			// On active les dropzones
			setDropZoneState(true);
			// On cr�e le bloc action associ� � l'�l�ment
			itemDragged = EditingUtility.createEditableBlockFromLibrary(element.selectedObject, mainCanvas);
			// On l'ajoute aux familles de FYFY
			GameObjectManager.bind(itemDragged);
			// exclude all UI elements that can disturb the drag from the EventSystem
			foreach (RaycastOnDrag child in itemDragged.GetComponentsInChildren<RaycastOnDrag>(true))
				child.GetComponent<Image>().raycastTarget = false;
		}
	}

	private string exportScriptToString(Transform scriptContainer, GameObject focusedArea)
    {
		string scriptsContent = scriptContainer.Find("Header").GetComponentInChildren<TMP_InputField>().text + " {";
		// on ignore le premier fils (le header) et le dernier (la drop zone)
		for (int i = 1; i < scriptContainer.childCount - 1; i++)
			scriptsContent += " " + exportBlockToString(scriptContainer.GetChild(i).GetComponent<Highlightable>(), focusedArea);
		if (scriptContainer.GetChild(scriptContainer.childCount-1).gameObject == focusedArea)
			scriptsContent += " ####";
		scriptsContent += " }\n";
		return scriptsContent;
	}

	private string exportBlockToString(Highlightable script, GameObject focusedArea)
    {
		if (script == null)
			return "";
		else {
			string export = "";
			if (script is BasicAction)
			{
				if (script.GetComponentInChildren<DropZone>(true).gameObject == focusedArea)
					export += "#### ";
				export += (script as BasicAction).actionType.ToString() +  ";";
			}
			else if (script is BaseCaptor)
			{
				ReplacementSlot localRS = script.GetComponent<ReplacementSlot>();
				if (localRS.gameObject == focusedArea)
					export += "##";
				export += (script as BaseCaptor).captorType.ToString();
				if (localRS.gameObject == focusedArea)
					export += "##";
			}
			else if (script is BaseOperator)
			{
				ReplacementSlot localRS = script.GetComponent<ReplacementSlot>();
				if (localRS.gameObject == focusedArea)
					export += "##";
				BaseOperator ope = script as BaseOperator;
				Transform container = script.transform.Find("Container");
				if (ope.operatorType == BaseOperator.OperatorType.NotOperator)
				{
					export += "NOT (";

					if (container.Find("EmptyConditionalSlot").GetComponent<ReplacementSlot>().gameObject == focusedArea)
						export += "####";
					else
						export += exportBlockToString(container.GetComponentInChildren<BaseCondition>(true), focusedArea);

					export += ")";
				}
				else if (ope.operatorType == BaseOperator.OperatorType.AndOperator)
				{
					export += "(";

					if (container.Find("EmptyConditionalSlot1").GetComponent<ReplacementSlot>().gameObject == focusedArea)
						export += "####";
					else
						export += exportBlockToString(container.GetChild(0).GetComponentInChildren<BaseCondition>(true), focusedArea);

					export += ") AND (";

					if (container.Find("EmptyConditionalSlot2").GetComponent<ReplacementSlot>().gameObject == focusedArea)
						export += "####";
					else
						export += exportBlockToString(container.GetChild(1).GetComponentInChildren<BaseCondition>(true), focusedArea);

					export += ")";
				}
				else if (ope.operatorType == BaseOperator.OperatorType.OrOperator)
				{
					export += "(";

					if (container.Find("EmptyConditionalSlot1").GetComponent<ReplacementSlot>().gameObject == focusedArea)
						export += "####";
					else
						export += exportBlockToString(container.GetChild(0).GetComponentInChildren<BaseCondition>(true), focusedArea);

					export += ") OR (";

					if (container.Find("EmptyConditionalSlot2").GetComponent<ReplacementSlot>().gameObject == focusedArea)
						export += "####";
					else
						export += exportBlockToString(container.GetChild(1).GetComponentInChildren<BaseCondition>(true), focusedArea);
					
					export += ")";
				}
				if (localRS.gameObject == focusedArea)
					export += "##";
			}
			else if (script is ControlElement)
			{
				if (script.transform.Find("Header").GetComponentInChildren<DropZone>(true).gameObject == focusedArea)
					export += "#### ";
				ControlElement control = script as ControlElement;
				if (script is WhileControl)
				{
					export += "WHILE (";

					if (script.transform.Find("ConditionContainer").Find("EmptyConditionalSlot").GetComponent<ReplacementSlot>().gameObject == focusedArea)
						export += "####";
					else
						export += exportBlockToString(script.transform.Find("ConditionContainer").GetComponentInChildren<BaseCondition>(true), focusedArea);

					export += ")";
				}
				else if (script is ForControl)
				{
					export += "REPEAT (";
					if (script.gameObject == focusedArea)
						export += "##";
					export += (script as ForControl).nbFor;
					if (script.gameObject == focusedArea)
						export += "##";
					export += ")";
				}
				else if (script is ForeverControl)
					export += "FOREVER";
				else if (script is IfControl)
				{
					export += "IF (";

					if (script.transform.Find("ConditionContainer").Find("EmptyConditionalSlot").GetComponent<ReplacementSlot>().gameObject == focusedArea)
						export += "####";
					else
						export += exportBlockToString(script.transform.Find("ConditionContainer").GetComponentInChildren<BaseCondition>(true), focusedArea);

					export += ")";
				}

				export += " {";
				
				Transform container = script.transform.Find("Container");
				// parcourir tous les enfants jusqu'� l'avant dernier (le dernier �tant la dropzone)
				for (int i = 0; i < container.childCount - 1; i++)
					export += " " + exportBlockToString(container.GetChild(i).GetComponent<BaseElement>(), focusedArea);
				if (container.GetChild(container.childCount - 1).gameObject == focusedArea)
					export += " ####";
				
				export += " }";
				
				if (script is IfElseControl)
				{
					export += " ELSE {";

					Transform containerElse = script.transform.Find("ElseContainer");
					// parcourir tous les enfants jusqu'� l'avant dernier (le dernier �tant la dropzone)
					for (int i = 0; i < containerElse.childCount - 1; i++)
						export += " " + exportBlockToString(containerElse.GetChild(i).GetComponent<BaseElement>(), focusedArea);
					if (containerElse.GetChild(containerElse.childCount - 1).gameObject == focusedArea)
						export += " ####";

					export += " }";
				}
			}
			return export;
		}
    }

	// Lors de la selection (d�but d'un drag) d'un bloc dans la zone d'�dition
	public void beginDragElementFromEditableScript(BaseEventData element)
    {
		// On verifie si c'est un �v�nement g�n�r� par le bouton gauche de la souris
		if (!Pause && gameData.dragDropEnabled && (element as PointerEventData).button == PointerEventData.InputButton.Left && element.selectedObject != null)
		{
			itemDragged = element.selectedObject;
			Transform parent = itemDragged.transform.parent;

			string content = exportBlockToString(itemDragged.GetComponent<Highlightable>(), null);

			GameObject removedFromArea = null;
			Transform nextBrother = itemDragged.transform.parent.GetChild(itemDragged.transform.GetSiblingIndex() + 1);
			if (nextBrother.GetComponentInChildren<DropZone>(true))
				removedFromArea = nextBrother.GetComponentInChildren<DropZone>(true).gameObject;
			else // means next brother is a replacement slot
				removedFromArea = nextBrother.GetComponentInChildren<ReplacementSlot>(true).gameObject;

			// On active les dropzones 
			setDropZoneState(true);

			// Update empty zone if required
			EditingUtility.manageEmptyZone(itemDragged);

			// On l'associe (temporairement) au Canvas Main
			itemDragged.transform.SetParent(mainCanvas.transform, false); // We need to perform it immediatelly to write change in statement
			itemDragged.transform.localScale = new Vector3(1.0f, 1.0f, 1.0f);
			GameObjectManager.refresh(itemDragged);

			// compute context after removing item dragged
			string context = exportScriptToString(nextBrother.GetComponentInParent<UIRootContainer>().transform, removedFromArea);

			// exclude all UI elements that can disturb the drag from the EventSystem
			foreach (RaycastOnDrag child in itemDragged.GetComponentsInChildren<RaycastOnDrag>(true))
				child.GetComponent<Image>().raycastTarget = false;
			// Restore action and subactions to inventory
			foreach (BaseElement actChild in itemDragged.GetComponentsInChildren<BaseElement>(true))
				GameObjectManager.addComponent<AddOne>(actChild.GetComponent<LibraryItemRef>().linkedTo);
			// Restore conditions to inventory
			foreach (BaseCondition condChild in itemDragged.GetComponentsInChildren<BaseCondition>(true))
				GameObjectManager.addComponent<AddOne>(condChild.GetComponent<LibraryItemRef>().linkedTo);

			// Rend le bouton d'execution actif (ou non)
			GameObjectManager.addComponent<NeedRefreshPlayButton>(MainLoop.instance.gameObject);

			refreshHierarchyContainers(parent.gameObject);
			GameObjectManager.addComponent<ActionPerformedForLRS>(itemDragged, new
			{
				verb = "deleted",
				objectType = "block",
				activityExtensions = new Dictionary<string, string>() {
					{ "content", content },
					{ "context", context }
				}
			});
		}
	}


	// Pendant le drag d'un bloc, permet de lui faire suivre le mouvement de la souris
	public void dragElement()
	{
		if(!Pause && gameData.dragDropEnabled && itemDragged != null) {
			itemDragged.transform.position = Input.mousePosition;
		}
	}

	private GameObject getFocusedDropArea()
    {
		foreach (GameObject go in f_dropZoneEnabled)
			if (go.transform.GetChild(0).gameObject.activeSelf)
				return go;
		foreach (GameObject go in f_replacementSlot)
			if (go.GetComponent<Outline>().enabled)
				return go;
		return null;
    }

	// Determine si l'element associ� � l'�v�nement EndDrag se trouve dans une zone de container ou non
	// D�truire l'objet si l�ch� hors d'un container
	public void endDragElement()
	{
		if (!Pause && gameData.dragDropEnabled && itemDragged != null)
		{
			// On commence par regarder s'il n'y a pas de container point�, dans ce cas on supprime l'objet drag
			GameObject dropArea = getFocusedDropArea();
			// On d�sactive les dropzones
			setDropZoneState(false);
			if (dropArea == null)
			{
				undoDrop();
				return;
			}
			else // sinon on ajoute l'�l�ment au container point�
			{
				if (dropArea.transform.IsChildOf(itemDragged.transform))
				{
					undoDrop();
					return;
				}

				if (addDraggedItemOnDropZone(dropArea))
				{
					// We restore all UI elements inside the EventSystem
					foreach (RaycastOnDrag child in itemDragged.GetComponentsInChildren<RaycastOnDrag>(true))
						child.GetComponent<Image>().raycastTarget = true;
					MainLoop.instance.StartCoroutine(pulseItem(itemDragged));
				}
			}
			// Rafraichissement de l'UI
			GameObjectManager.addComponent<NeedRefreshPlayButton>(MainLoop.instance.gameObject);

			itemDragged = null;
		}
	}

	// Add the dragged item on the drop area
	private bool addDraggedItemOnDropZone (GameObject dropArea)
    {
		string content = exportBlockToString(itemDragged.GetComponent<Highlightable>(), null);
		string context = exportScriptToString(dropArea.GetComponentInParent<UIRootContainer>().transform, dropArea);

		if (!EditingUtility.addItemOnDropArea(itemDragged, dropArea))
		{
			undoDrop();
			return false;
		}

		GameObjectManager.addComponent<NeedRefreshPlayButton>(MainLoop.instance.gameObject);

		// update limit bloc
		foreach (BaseElement actChild in itemDragged.GetComponentsInChildren<BaseElement>(true))
			GameObjectManager.addComponent<Dropped>(actChild.gameObject);
		foreach (BaseCondition condChild in itemDragged.GetComponentsInChildren<BaseCondition>(true))
			GameObjectManager.addComponent<Dropped>(condChild.gameObject);

		// refresh all the hierarchy of parent containers
		refreshHierarchyContainers(itemDragged);
		// Update size of parent GameObject
		GameObjectManager.addComponent<RefreshSizeOfEditableContainer>(MainLoop.instance.gameObject);

		// Lance le son de d�p�t du block d'action
		audioSource.Play();

		GameObjectManager.addComponent<ActionPerformedForLRS>(itemDragged, new
		{
			verb = "inserted",
			objectType = "block",
			activityExtensions = new Dictionary<string, string>() {
				{ "content", content },
				{ "context", context }
			}
		});

		return true;
	}

	// suppression de l'objet en cours de drag
	private void undoDrop()
    {
		// Suppression des familles de FYFY
		GameObjectManager.unbind(itemDragged);
		// D�struction du bloc
		UnityEngine.Object.Destroy(itemDragged);
		itemDragged = null;
	}

	// D�clenche la suppression de l'�l�ment
	public void deleteElement(GameObject elementToDelete)
	{
		// On v�rifie qu'il y a bien un objet point� pour la suppression
		if(!Pause && gameData.dragDropEnabled && elementToDelete != null)
		{
			string content = exportBlockToString(elementToDelete.GetComponent<Highlightable>(), null);

			GameObject removedFromArea = null;
			Transform nextBrother = elementToDelete.transform.parent.GetChild(elementToDelete.transform.GetSiblingIndex() + 1);
			if (nextBrother.GetComponentInChildren<DropZone>(true))
				removedFromArea = nextBrother.GetComponentInChildren<DropZone>(true).gameObject;
			else // means next brother is a replacement slot
				removedFromArea = nextBrother.GetComponentInChildren<ReplacementSlot>(true).gameObject;

			// R�activation d'une EmptyZone si n�cessaire
			EditingUtility.manageEmptyZone(elementToDelete);

			// On l'associe (temporairement) au Canvas Main
			elementToDelete.transform.SetParent(mainCanvas.transform, false); // We need to perform it immediatelly to write change in statement
			elementToDelete.transform.localScale = new Vector3(1.0f, 1.0f, 1.0f);
			GameObjectManager.refresh(elementToDelete);
			// compute context after removing item dragged
			string context = exportScriptToString(nextBrother.GetComponentInParent<UIRootContainer>().transform, removedFromArea);

			//On associe � l'�l�ment le component ResetBlocLimit pour d�clancher le script de destruction de l'�l�ment
			GameObjectManager.addComponent<ResetBlocLimit>(elementToDelete);
			GameObjectManager.addComponent<NeedRefreshPlayButton>(MainLoop.instance.gameObject);
			// refresh all the hierarchy of parent containers
			refreshHierarchyContainers(elementToDelete);

			NeedToDelete ntd = elementToDelete.GetComponent<NeedToDelete>();
			if (ntd == null || !ntd.silent)
			{
				GameObjectManager.addComponent<ActionPerformedForLRS>(MainLoop.instance.gameObject, new
				{
					verb = "deleted",
					objectType = "block",
					activityExtensions = new Dictionary<string, string>() {
					{ "content", content },
					{ "context", context }
				}
				});
			}
		}
	}

	private void selectNewDefaultDropZone(GameObject newDropZone)
    {
		// define this new drop zone as the default
		lastDropZoneUsed = newDropZone.transform.gameObject;
		// remove old trigger component
		foreach (Selected selectedDZ in newDropZone.GetComponents<Selected>())
			GameObjectManager.removeComponent(selectedDZ);
	}

	// Refresh the hierarchy (parent by parent) from elementToRefresh. Used in Control prefabs (If, For, ...)
	public void refreshHierarchyContainers(GameObject elementToRefresh)
    {
		// refresh all the hierarchy of parent containers
		Transform parent = elementToRefresh.transform.parent;
		while (parent is RectTransform)
		{
			MainLoop.instance.StartCoroutine(forceUIRefresh((RectTransform)parent));
			parent = parent.parent;
		}
	}

	// Besoin d'attendre l'update pour effectuer le recalcul de la taille des containers
	private IEnumerator forceUIRefresh(RectTransform bloc)
	{
		yield return null;
		yield return null;
		LayoutRebuilder.ForceRebuildLayoutImmediate(bloc);
	}

	// Si double clic sur l'�l�ment de la biblioth�que (voir l'inspector), ajoute le bloc d'action au dernier container utilis�
	public void checkDoubleClick(BaseEventData element)
    {
		if (!Pause && gameData.dragDropEnabled && doubleClick() && !itemDragged)
		{
			// if no drop zone used, try to get the last
			if (lastDropZoneUsed == null)
				lastDropZoneUsed = f_dropArea.getAt(f_dropArea.Count-1);
			// be sure the lastDropZone is defined
			if (lastDropZoneUsed != null)
			{
				// On cr�e le bloc action
				itemDragged = EditingUtility.createEditableBlockFromLibrary(element.selectedObject, mainCanvas);
				// On l'ajoute aux familles de FYFY
				GameObjectManager.bind(itemDragged);
				// On l'envoie sur la derni�re dropzone utilis�e
				addDraggedItemOnDropZone(lastDropZoneUsed);
				// refresh all the hierarchy of parent containers
				refreshHierarchyContainers(lastDropZoneUsed);

				if (viewLastDropZone != null)
					MainLoop.instance.StopCoroutine(viewLastDropZone);
				MainLoop.instance.StartCoroutine(focusOnLastDropZoneUsed());
				MainLoop.instance.StartCoroutine(pulseItem(itemDragged));

				// Rafraichissement de l'UI
				GameObjectManager.addComponent<NeedRefreshPlayButton>(MainLoop.instance.gameObject);
				itemDragged = null;
			}
		}
	}

	private IEnumerator pulseItem(GameObject newItem)
    {
		float initScaleX = newItem.transform.localScale.x;
		newItem.transform.localScale = new Vector3(newItem.transform.localScale.x + 0.3f, newItem.transform.localScale.y, newItem.transform.localScale.z);
		while (newItem.transform.localScale.x > initScaleX)
		{
			newItem.transform.localScale = new Vector3(newItem.transform.localScale.x-0.01f, newItem.transform.localScale.y, newItem.transform.localScale.z);
			yield return null;
		}
	}

	private IEnumerator focusOnLastDropZoneUsed()
    {
		yield return new WaitForSeconds(.25f);

		RectTransform editableCanvas = editableContainers.parent as RectTransform;
		// get last drop zone reference (depends if last drop zone is a replacement slot, a drop zone of an action or a drop zone of a control block
		RectTransform lastDropRef = null;
		if (lastDropZoneUsed.GetComponent<DropZone>())
		{
			if (lastDropZoneUsed.transform.parent.GetComponent<BaseElement>()) // BasicAction
				lastDropRef = lastDropZoneUsed.transform.parent as RectTransform; // target is the parent
			else if (lastDropZoneUsed.transform.parent.parent.GetComponent<ControlElement>() && lastDropZoneUsed.transform.parent.GetSiblingIndex() == 1) // the dropArea of the first child of a Control block
				lastDropRef = lastDropZoneUsed.transform.parent.parent as RectTransform; // target is the grandparent
		}
		else
			lastDropRef = lastDropZoneUsed.transform as RectTransform; // This is a replacement slot

		float lastDropZoneUsedY = Mathf.Abs(editableContainers.InverseTransformPoint(lastDropRef.transform.position).y);
		float lastDropZoneUsedX = Mathf.Abs(editableContainers.InverseTransformPoint(lastDropRef.transform.position).x);

		Vector2 targetAnchoredPosition = new Vector2(editableContainers.anchoredPosition.x, editableContainers.anchoredPosition.y);
		// we auto focus on last drop zone used only if it is not visible
		if (lastDropZoneUsedY - editableContainers.anchoredPosition.y < 0 || lastDropZoneUsedY - editableContainers.anchoredPosition.y > editableCanvas.rect.height)
		{
			// check if last drop zone used is too high
			if (lastDropZoneUsedY - editableContainers.anchoredPosition.y < 0)
			{
				targetAnchoredPosition = new Vector2(
					targetAnchoredPosition.x,
					lastDropZoneUsedY - (lastDropRef.rect.height * 2f) // move view a little bit higher than last drop zone position
				);
			}
			// check if last drop zone used is too low
			else if (lastDropZoneUsedY - editableContainers.anchoredPosition.y > editableCanvas.rect.height)
			{
				targetAnchoredPosition = new Vector2(
					targetAnchoredPosition.x,
					-editableCanvas.rect.height + lastDropZoneUsedY + lastDropRef.rect.height / 2
				);
			}

			// same for x pos
			if (lastDropZoneUsedX + editableContainers.anchoredPosition.x < 0)
			{
				targetAnchoredPosition = new Vector2(
					-lastDropZoneUsedX + lastDropRef.rect.width,
					targetAnchoredPosition.y
				);
			}
			else if (lastDropZoneUsedX + editableContainers.anchoredPosition.x > editableCanvas.rect.width)
			{
				targetAnchoredPosition = new Vector2(
					editableCanvas.rect.width - lastDropZoneUsedX - lastDropRef.rect.width,
					targetAnchoredPosition.y
				);
			}

			float distance = Vector2.Distance(editableContainers.anchoredPosition, targetAnchoredPosition);
			while (Vector2.Distance(editableContainers.anchoredPosition, targetAnchoredPosition) > 0.1f)
			{
				editableContainers.anchoredPosition = Vector2.MoveTowards(editableContainers.anchoredPosition, targetAnchoredPosition, distance / 10);
				yield return null;
			}
		}
	}


	// V�rifie si le double click a eu lieu
	private bool doubleClick()
	{
		// check double click
		// On met � jour le timer du dernier click
		// et on retourne la r�ponse
		if (Time.time - lastClickTime < catchTime)
        {
			lastClickTime = Time.time-catchTime;
			return true;
		}
        else
        {
			lastClickTime = Time.time;
			return false;
		}
	}

	// see inputFiels in ForBloc prefab in inspector
	public void onlyPositiveInteger(GameObject forBlock, string newValue)
	{
		int oldValue = forBlock.GetComponent<ForControl>().nbFor;
		Transform input = forBlock.transform.Find("Header");
		int res;
		bool success = Int32.TryParse(newValue, out res);
		if (!success || (success && Int32.Parse(newValue) < 0))
		{
			input.GetComponent<TMP_InputField>().text = "0";
			res = 0;
		}
		forBlock.GetComponent<ForControl>().nbFor = res;
		// compute context
		string context = exportScriptToString(forBlock.GetComponentInParent<UIRootContainer>().transform, forBlock);

		GameObjectManager.addComponent<ActionPerformedForLRS>(forBlock, new
		{
			verb = "modified",
			objectType = "block",
			activityExtensions = new Dictionary<string, string>() {
				{ "context", context },
				{ "oldValue", oldValue.ToString()},
				{ "value", res.ToString()}
			}
		});
	}
}