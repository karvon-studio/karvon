use semver_lite::Version;

#[test]
fn orders_by_major_then_minor_then_patch() {
    let mut versions = vec![
        Version::new(1, 10, 0),
        Version::new(1, 2, 9),
        Version::new(0, 9, 9),
        Version::new(1, 2, 10),
    ];
    versions.sort();
    assert_eq!(
        versions,
        [
            Version::new(0, 9, 9),
            Version::new(1, 2, 9),
            Version::new(1, 2, 10),
            Version::new(1, 10, 0)
        ]
    );
}

#[test]
fn bumps_reset_the_lower_parts() {
    let v = Version::new(1, 4, 7);
    assert_eq!(v.bump_major(), Version::new(2, 0, 0));
    assert_eq!(v.bump_minor(), Version::new(1, 5, 0));
    assert_eq!(v.bump_patch(), Version::new(1, 4, 8));
}

#[test]
fn caret_allows_the_same_major() {
    let req = Version::new(1, 2, 3);
    assert!(Version::new(1, 2, 3).matches_caret(&req));
    assert!(Version::new(1, 9, 0).matches_caret(&req));
    assert!(!Version::new(1, 2, 2).matches_caret(&req));
    assert!(!Version::new(2, 0, 0).matches_caret(&req));
}

#[test]
fn caret_below_one_allows_the_same_minor() {
    let req = Version::new(0, 2, 3);
    assert!(Version::new(0, 2, 9).matches_caret(&req));
    assert!(!Version::new(0, 3, 0).matches_caret(&req));
    assert!(!Version::new(1, 2, 3).matches_caret(&req));
}

#[test]
fn caret_below_zero_point_one_allows_only_itself() {
    let req = Version::new(0, 0, 3);
    assert!(Version::new(0, 0, 3).matches_caret(&req));
    assert!(!Version::new(0, 0, 4).matches_caret(&req));
}
