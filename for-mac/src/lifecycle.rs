use serde::{Deserialize, Serialize};
use std::fmt;

#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
pub enum LifecyclePhase {
    Standby,
    Waking,
    Warming,
    Running,
    Sleeping,
}

impl LifecyclePhase {
    pub fn can_transition_to(self, next: Self) -> bool {
        matches!(
            (self, next),
            (Self::Standby, Self::Waking)
                | (Self::Waking, Self::Warming)
                | (Self::Waking, Self::Standby)
                | (Self::Warming, Self::Running)
                | (Self::Warming, Self::Standby)
                | (Self::Running, Self::Sleeping)
                | (Self::Running, Self::Standby)
                | (Self::Sleeping, Self::Standby)
        )
    }
}

impl fmt::Display for LifecyclePhase {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        let value = match self {
            Self::Standby => "Standby",
            Self::Waking => "Waking",
            Self::Warming => "Warming",
            Self::Running => "Running",
            Self::Sleeping => "Sleeping",
        };
        f.write_str(value)
    }
}

#[cfg(test)]
mod tests {
    #[test]
    fn phase_transitions_follow_server_lifecycle() {
        use super::LifecyclePhase;
        assert!(LifecyclePhase::Standby.can_transition_to(LifecyclePhase::Waking));
        assert!(LifecyclePhase::Waking.can_transition_to(LifecyclePhase::Warming));
        assert!(LifecyclePhase::Warming.can_transition_to(LifecyclePhase::Running));
        assert!(LifecyclePhase::Running.can_transition_to(LifecyclePhase::Sleeping));
        assert!(!LifecyclePhase::Standby.can_transition_to(LifecyclePhase::Running));
    }
}
