using UnityEngine;
using FYFY;
using TMPro;
using System.Collections;
using System.Collections.Generic;
using System.Data;

/// <summary>
/// Manage CurrentAction components, parse scripts and define first action, next actions, evaluate boolean expressions (if and while)...
/// </summary>
public class CurrentActionManager : FSystem
{
	private Family f_executionReady = FamilyManager.getFamily(new AllOfComponents(typeof(ExecutablePanelReady)));
	private Family f_ends = FamilyManager.getFamily(new AllOfComponents(typeof(NewEnd)));
	private Family f_newStep = FamilyManager.getFamily(new AllOfComponents(typeof(NewStep)));
    private Family f_currentActions = FamilyManager.getFamily(new AllOfComponents(typeof(BasicAction),typeof(LibraryItemRef), typeof(CurrentAction)));
	private Family f_player = FamilyManager.getFamily(new AllOfComponents(typeof(ScriptRef),typeof(Position)), new AnyOfTags("Player"));

	private Family f_wall = FamilyManager.getFamily(new AllOfComponents(typeof(Position)), new AnyOfTags("Wall"));
	private Family f_drone = FamilyManager.getFamily(new AllOfComponents(typeof(ScriptRef), typeof(Position)), new AnyOfTags("Drone"));
	private Family f_door = FamilyManager.getFamily(new AllOfComponents(typeof(ActivationSlot), typeof(Position)), new AnyOfTags("Door"));
	private Family f_redDetector = FamilyManager.getFamily(new AllOfComponents(typeof(Rigidbody), typeof(Detector), typeof(Position)));
	private Family f_activableConsole = FamilyManager.getFamily(new AllOfComponents(typeof(Activable), typeof(Position), typeof(AudioSource)));
	private Family f_exit = FamilyManager.getFamily(new AllOfComponents(typeof(Position), typeof(AudioSource)), new AnyOfTags("Exit"));

	private Family f_playingMode = FamilyManager.getFamily(new AllOfComponents(typeof(PlayMode)));
	private Family f_editingMode = FamilyManager.getFamily(new AllOfComponents(typeof(EditMode)));

	public static CurrentActionManager instance;

	public CurrentActionManager()
	{
		instance = this;
	}

	protected override void onStart()
	{
		f_executionReady.addEntryCallback(initFirstsActions);
		f_newStep.addEntryCallback(delegate { onNewStep(); });
		f_editingMode.addEntryCallback(delegate {
			// remove all player's current actions
			foreach (GameObject currentAction in f_currentActions)
				if (currentAction.GetComponent<CurrentAction>().agent.CompareTag("Player"))
					GameObjectManager.removeComponent<CurrentAction>(currentAction);
		});
		f_playingMode.addEntryCallback(delegate {
			// reset inaction counters
			foreach (GameObject robot in f_player)
				robot.GetComponent<ScriptRef>().nbOfInactions = 0;
		});
	}

	private void initFirstsActions(GameObject go)
	{
		// init first action if no ends occur (possible for scripts with bad condition)
		if (f_ends.Count <= 0)
		{
			// init currentAction on the first action of players
			bool atLeastOneFirstAction = false;
			foreach (GameObject player in f_player)
				if (addCurrentActionOnFirstAction(player) != null)
					atLeastOneFirstAction = true;
			if (!atLeastOneFirstAction)
			{
				GameObjectManager.addComponent<EditMode>(MainLoop.instance.gameObject);
			}
			else
			{
				// init currentAction on the first action of ennemies
				bool forceNewStep = false;
				foreach (GameObject drone in f_drone)
					if (!drone.GetComponent<ScriptRef>().executableScript.GetComponentInChildren<CurrentAction>() && !drone.GetComponent<ScriptRef>().scriptFinished)
						addCurrentActionOnFirstAction(drone);
					else
						forceNewStep = true; // will move currentAction on next action

				if (forceNewStep)
					onNewStep();
			}
		}

		GameObjectManager.removeComponent<ExecutablePanelReady>(go);
	}

	private GameObject addCurrentActionOnFirstAction(GameObject agent)
    {
		GameObject firstAction = null;
		// try to get the first action
		Transform container = agent.GetComponent<ScriptRef>().executableScript.transform;
		if (container.childCount > 0)
			firstAction = getFirstActionOf(container.GetChild(0).gameObject, agent);

		if (firstAction != null)
		{
			// Set this action as CurrentAction
			GameObjectManager.addComponent<CurrentAction>(firstAction, new { agent = agent });
		}

		return firstAction;
	}

	// Return true if "condition" is valid and false otherwise
	private bool ifValid(List<string> condition, GameObject agent)
	{

		string cond = "";
		for (int i = 0; i < condition.Count; i++)
		{
			if (condition[i] == "(" || condition[i] == ")" || condition[i] == "OR" || condition[i] == "AND" || condition[i] == "NOT")
			{
				cond = cond + condition[i] + " ";
			}
			else
			{
				cond = cond + checkCaptor(condition[i], agent) + " ";
			}
		}

		DataTable dt = new DataTable();
		var v = dt.Compute(cond, "");
		bool result;
		try
		{
			result = bool.Parse(v.ToString());
		}
		catch
		{
			result = false;
		}
		return result;
	}

	// return true if the captor is true, and false otherwise
	private bool checkCaptor(string ele, GameObject agent)
	{

		bool ifok = false;
		// get absolute target position depending on player orientation and relative direction to observe
		// On commence par identifier quelle case doit �tre regard�e pour voir si la condition est respect�e
		Vector2 vec = new Vector2();
		switch (agent.GetComponent<Direction>().direction)
		{
			case Direction.Dir.North:
				vec = new Vector2(0, -1);
				break;
			case Direction.Dir.South:
				vec = new Vector2(0, 1);
				break;
			case Direction.Dir.East:
				vec = new Vector2(1, 0);
				break;
			case Direction.Dir.West:
				vec = new Vector2(-1, 0);
				break;
		}

		// check target position
		switch (ele)
		{
			case "Wall": // walls
				foreach (GameObject wall in f_wall)
					if (wall.GetComponent<Position>().x == agent.GetComponent<Position>().x + vec.x &&
					 wall.GetComponent<Position>().y == agent.GetComponent<Position>().y + vec.y && wall.GetComponent<Renderer>().enabled)
						ifok = true;
				break;
			case "FieldGate": // doors
				foreach (GameObject door in f_door)
					if (door.GetComponent<Position>().x == agent.GetComponent<Position>().x + vec.x &&
					 door.GetComponent<Position>().y == agent.GetComponent<Position>().y + vec.y)
						ifok = true;
				break;
			case "Enemie": // ennemies
				foreach (GameObject drone in f_drone)
					if (drone.GetComponent<Position>().x == agent.GetComponent<Position>().x + vec.x &&
						drone.GetComponent<Position>().y == agent.GetComponent<Position>().y + vec.y)
						ifok = true;
				break;
			case "Terminal": // consoles
				foreach (GameObject console in f_activableConsole)
				{
					vec = new Vector2(0, 0);
					if (console.GetComponent<Position>().x == agent.GetComponent<Position>().x + vec.x &&
						console.GetComponent<Position>().y == agent.GetComponent<Position>().y + vec.y)
						ifok = true;
				}
				break;
			case "RedArea": // detectors
				foreach (GameObject detector in f_redDetector)
					if (detector.GetComponent<Position>().x == agent.GetComponent<Position>().x + vec.x &&
					 detector.GetComponent<Position>().y == agent.GetComponent<Position>().y + vec.y)
						ifok = true;
				break;
			case "Exit": // exits
				foreach (GameObject exit in f_exit)
				{
					vec = new Vector2(0, 0);
					if (exit.GetComponent<Position>().x == agent.GetComponent<Position>().x + vec.x &&
					 exit.GetComponent<Position>().y == agent.GetComponent<Position>().y + vec.y)
						ifok = true;
				}
				break;
		}
		return ifok;

	}

	// get first action inside "action", it could be control structure (if, for...) => recursive call
	private GameObject getFirstActionOf(GameObject action, GameObject agent)
	{
		if (action == null)
			return null;
		if (action.GetComponent<BasicAction>())
			return action;
		else
		{
			// check if action is a IfControl
			if (action.GetComponent<IfControl>())
			{
				IfControl ifCont = action.GetComponent<IfControl>();
				// check if this IfControl include a child and if condition is evaluated to true
				if (ifCont.firstChild != null && ifValid(ifCont.condition, agent))
					// get first action of its first child (could be if, for...)
					return getFirstActionOf(ifCont.firstChild, agent);
				else if (action.GetComponent<IfElseControl>() && action.GetComponent<IfElseControl>().firstChild != null)
					return getFirstActionOf(action.GetComponent<IfElseControl>().elseFirstChild, agent);
				else
					// this if doesn't contain action or its condition is false => get first action of next action (could be if, for...)
					return getFirstActionOf(ifCont.next, agent);
			}
			// check if action is a WhileControl
			else if (action.GetComponent<WhileControl>())
			{
				WhileControl whileCont = action.GetComponent<WhileControl>();
				// check if this WhileControl include a child and if condition is evaluated to true
				if (whileCont.firstChild != null && ifValid(whileCont.condition, agent))
					// get first action of its first child (could be if, for...)
					return getFirstActionOf(whileCont.firstChild, agent);
				else
					// this if doesn't contain action or its condition is false => get first action of next action (could be if, for...)
					return getFirstActionOf(whileCont.next, agent);
			}
			// check if action is a ForControl
			else if (action.GetComponent<ForControl>())
			{
				ForControl forCont = action.GetComponent<ForControl>();
				// check if this ForControl include a child and nb iteration != 0 and end loop not reached
				if (forCont.firstChild != null && forCont.nbFor != 0 && forCont.currentFor < forCont.nbFor)
				{
					forCont.currentFor++;
					forCont.transform.GetChild(1).GetChild(1).GetComponent<TMP_InputField>().text = (forCont.currentFor).ToString() + " / " + forCont.nbFor.ToString();
					// get first action of its first child (could be if, for...)
					return getFirstActionOf(forCont.firstChild, agent);
				}
				else
					// this for doesn't contain action or nb iteration == 0 or end loop reached => get first action of next action (could be if, for...)
					return getFirstActionOf(forCont.next, agent);
			}
			// check if action is a ForeverControl
			else if (action.GetComponent<ForeverControl>())
			{
				// always return firstchild of this ForeverControl
				return getFirstActionOf(action.GetComponent<ForeverControl>().firstChild, agent);
			}
		}
		return null;
	}

	// one step consists in removing the current actions this frame and adding new CurrentAction components next frame
	private void onNewStep(){
		GameObject nextAction;
		foreach(GameObject currentActionGO in f_currentActions){
			CurrentAction currentAction = currentActionGO.GetComponent<CurrentAction>();
			nextAction = getNextAction(currentActionGO, currentAction.agent);
			// check if we reach last action of a drone
			if(nextAction == null && currentActionGO.GetComponent<CurrentAction>().agent.CompareTag("Drone"))
				currentActionGO.GetComponent<CurrentAction>().agent.GetComponent<ScriptRef>().scriptFinished = true;
			else if(nextAction != null){
				//ask to add CurrentAction on next frame => this frame we will remove current CurrentActions
				MainLoop.instance.StartCoroutine(delayAddCurrentAction(nextAction, currentAction.agent));
			}
			GameObjectManager.removeComponent<CurrentAction>(currentActionGO);
		}
	}

	// return the next action to execute, return null if no next action available
	private GameObject getNextAction(GameObject currentAction, GameObject agent){
		BasicAction current_ba = currentAction.GetComponent<BasicAction>();
		if (current_ba != null)
		{
			// if next is not defined or is a BasicAction we return it
			if(current_ba.next == null || current_ba.next.GetComponent<BasicAction>())
				return current_ba.next;
			else
				return getNextAction(current_ba.next, agent);
		}
		else if (currentAction.GetComponent<WhileControl>())
        {
			if(ifValid(currentAction.GetComponent<WhileControl>().condition, agent))
            {
				if (currentAction.GetComponent<WhileControl>().firstChild.GetComponent<BasicAction>())
					return currentAction.GetComponent<WhileControl>().firstChild;
				else
					return getNextAction(currentAction.GetComponent<WhileControl>().firstChild, agent);
			}
            else
            {
				if (currentAction.GetComponent<WhileControl>().next == null || currentAction.GetComponent<WhileControl>().next.GetComponent<BasicAction>())
					return currentAction.GetComponent<WhileControl>().next;
				else
					return getNextAction(currentAction.GetComponent<WhileControl>().next, agent);
			}
		}
		// currentAction is not a BasicAction
		// check if it is a ForAction
		else if(currentAction.GetComponent<ForControl>()){
			ForControl forAct = currentAction.GetComponent<ForControl>();
			// ForAction reach the number of iterations
			if (!forAct.gameObject.GetComponent<WhileControl>() && forAct.currentFor >= forAct.nbFor){
				// reset nb iteration to 0
				forAct.currentFor = 0;
				forAct.transform.GetChild(1).GetChild(1).GetComponent<TMP_InputField>().text = (forAct.currentFor).ToString() + " / " + forAct.nbFor.ToString();
				// return next action
				if(forAct.next == null || forAct.next.GetComponent<BasicAction>())
					return forAct.next;
				else
					return getNextAction(forAct.next , agent);
			}
			// iteration are available
			else{
				// in case ForAction has no child
				if (forAct.firstChild == null)
				{
					if (!forAct.gameObject.GetComponent<WhileControl>()) {
						// reset nb iteration to 0
						forAct.currentFor = 0;
						forAct.transform.GetChild(1).GetChild(1).GetComponent<TMP_InputField>().text = (forAct.currentFor).ToString() + " / " + forAct.nbFor.ToString();
					}
					// return next action
					if (forAct.next == null || forAct.next.GetComponent<BasicAction>())
						return forAct.next;
					else
						return getNextAction(forAct.next, agent);
				}
				else
				// return first child
				{
					// add one iteration
					forAct.currentFor++;
					forAct.transform.GetChild(1).GetChild(1).GetComponent<TMP_InputField>().text = (forAct.currentFor).ToString() + " / " + forAct.nbFor.ToString();
					// return first child
					if (forAct.firstChild.GetComponent<BasicAction>())
						return forAct.firstChild;
					else
						return getNextAction(forAct.firstChild, agent);
				}
			}
		}
		// check if it is a IfAction
		else if(currentAction.GetComponent<IfControl>()){
			// check if IfAction has a first child and condition is true
			IfControl ifAction = currentAction.GetComponent<IfControl>();
			if (ifAction.firstChild != null && ifValid(ifAction.condition, agent)){ 
				// return first action
				if(ifAction.firstChild.GetComponent<BasicAction>())
					return ifAction.firstChild;
				else
					return getNextAction(ifAction.firstChild, agent);				
			}
			else if (currentAction.GetComponent<IfElseControl>() && currentAction.GetComponent<IfElseControl>().firstChild != null)
				return currentAction.GetComponent<IfElseControl>().elseFirstChild;
			else
			{
				// return next action
				if(ifAction.next == null || ifAction.next.GetComponent<BasicAction>()){
					return ifAction.next;
				}
				else{
					return getNextAction(ifAction.next , agent);
				}				
			}
		}
		// check if it is a ForeverAction
		else if(currentAction.GetComponent<ForeverControl>()){
			ForeverControl foreverAction = currentAction.GetComponent<ForeverControl>();
			if (foreverAction.firstChild == null || foreverAction.firstChild.GetComponent<BasicAction>())
				return foreverAction.firstChild;
			else
				return getNextAction(foreverAction.firstChild, agent);
		}

		return null;
	}

	private IEnumerator delayAddCurrentAction(GameObject nextAction, GameObject agent)
	{
		yield return null; // we add new CurrentAction next frame otherwise families are not notified to this adding because at the begining of this frame GameObject already contains CurrentAction
		GameObjectManager.addComponent<CurrentAction>(nextAction, new { agent = agent });
	}
}