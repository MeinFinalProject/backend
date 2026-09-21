using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ta.Backend.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AcademicAttendanceAndEnrollment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "academic_term",
                columns: table => new
                {
                    academic_term_id = table.Column<Guid>(type: "uuid", nullable: false),
                    academic_term_name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    academic_term_start = table.Column<DateOnly>(type: "date", nullable: false),
                    academic_term_end = table.Column<DateOnly>(type: "date", nullable: false),
                    academic_term_minimum_attendance = table.Column<decimal>(type: "numeric", nullable: false),
                    academic_term_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_academic_term", x => x.academic_term_id);
                    table.CheckConstraint("ck_term_dates", "academic_term_end >= academic_term_start AND academic_term_minimum_attendance BETWEEN 0 AND 100");
                });

            migrationBuilder.CreateTable(
                name: "account",
                columns: table => new
                {
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_email = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    account_password_hash = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    account_name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    account_role = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    account_status = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    account_failed_logins = table.Column<int>(type: "integer", nullable: false),
                    account_locked_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    account_created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_account", x => x.account_id);
                    table.CheckConstraint("ck_account_role", "account_role IN ('administrator','lecturer','student')");
                    table.CheckConstraint("ck_account_status", "account_status IN ('pending','approved','disabled','rejected')");
                });

            migrationBuilder.CreateTable(
                name: "audit_record",
                columns: table => new
                {
                    audit_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    audit_actor = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    audit_action = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    audit_resource = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    audit_details = table.Column<string>(type: "jsonb", nullable: false),
                    audit_occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_audit_record", x => x.audit_record_id);
                });

            migrationBuilder.CreateTable(
                name: "classroom",
                columns: table => new
                {
                    classroom_id = table.Column<Guid>(type: "uuid", nullable: false),
                    classroom_code = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    classroom_name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    classroom_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_classroom", x => x.classroom_id);
                });

            migrationBuilder.CreateTable(
                name: "study_program",
                columns: table => new
                {
                    study_program_id = table.Column<Guid>(type: "uuid", nullable: false),
                    study_program_code = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    study_program_name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    study_program_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_study_program", x => x.study_program_id);
                });

            migrationBuilder.CreateTable(
                name: "account_session",
                columns: table => new
                {
                    account_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_session_token_hash = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    account_session_expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    account_session_revoked = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_account_session", x => x.account_session_id);
                    table.ForeignKey(
                        name: "fk_account_session_account_id",
                        column: x => x.account_id,
                        principalTable: "account",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "lecturer",
                columns: table => new
                {
                    lecturer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lecturer_number = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_lecturer", x => x.lecturer_id);
                    table.ForeignKey(
                        name: "fk_lecturer_account_id",
                        column: x => x.account_id,
                        principalTable: "account",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "device_room_assignment",
                columns: table => new
                {
                    device_room_assignment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_id = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    classroom_id = table.Column<Guid>(type: "uuid", nullable: false),
                    device_room_assignment_valid_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    device_room_assignment_valid_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_device_room_assignment", x => x.device_room_assignment_id);
                    table.CheckConstraint("ck_device_room_interval", "device_room_assignment_valid_until IS NULL OR device_room_assignment_valid_until >= device_room_assignment_valid_from");
                    table.ForeignKey(
                        name: "fk_device_room_assignment_classroom_id",
                        column: x => x.classroom_id,
                        principalTable: "classroom",
                        principalColumn: "classroom_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_device_room_assignment_device_id",
                        column: x => x.device_id,
                        principalTable: "device",
                        principalColumn: "device_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "course",
                columns: table => new
                {
                    course_id = table.Column<Guid>(type: "uuid", nullable: false),
                    study_program_id = table.Column<Guid>(type: "uuid", nullable: false),
                    course_code = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    course_name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    course_credits = table.Column<int>(type: "integer", nullable: false),
                    course_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_course", x => x.course_id);
                    table.CheckConstraint("ck_course_credits", "course_credits BETWEEN 1 AND 24");
                    table.ForeignKey(
                        name: "fk_course_study_program_id",
                        column: x => x.study_program_id,
                        principalTable: "study_program",
                        principalColumn: "study_program_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "student",
                columns: table => new
                {
                    student_id = table.Column<Guid>(type: "uuid", nullable: false),
                    account_id = table.Column<Guid>(type: "uuid", nullable: false),
                    study_program_id = table.Column<Guid>(type: "uuid", nullable: false),
                    advisor_lecturer_id = table.Column<Guid>(type: "uuid", nullable: true),
                    student_number = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    student_identity_id = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    student_phone = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    student_entry_year = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_student", x => x.student_id);
                    table.ForeignKey(
                        name: "fk_student_account_id",
                        column: x => x.account_id,
                        principalTable: "account",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_student_advisor_lecturer_id",
                        column: x => x.advisor_lecturer_id,
                        principalTable: "lecturer",
                        principalColumn: "lecturer_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_student_study_program_id",
                        column: x => x.study_program_id,
                        principalTable: "study_program",
                        principalColumn: "study_program_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "academic_class",
                columns: table => new
                {
                    academic_class_id = table.Column<Guid>(type: "uuid", nullable: false),
                    academic_term_id = table.Column<Guid>(type: "uuid", nullable: false),
                    course_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lecturer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    academic_class_name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    academic_class_capacity = table.Column<int>(type: "integer", nullable: false),
                    academic_class_active = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_academic_class", x => x.academic_class_id);
                    table.CheckConstraint("ck_class_capacity", "academic_class_capacity BETWEEN 1 AND 1000");
                    table.ForeignKey(
                        name: "fk_academic_class_academic_term_id",
                        column: x => x.academic_term_id,
                        principalTable: "academic_term",
                        principalColumn: "academic_term_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_academic_class_course_id",
                        column: x => x.course_id,
                        principalTable: "course",
                        principalColumn: "course_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_academic_class_lecturer_id",
                        column: x => x.lecturer_id,
                        principalTable: "lecturer",
                        principalColumn: "lecturer_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "biometric_enrollment",
                columns: table => new
                {
                    biometric_enrollment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    student_id = table.Column<Guid>(type: "uuid", nullable: false),
                    biometric_enrollment_status = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    biometric_enrollment_consented_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    biometric_enrollment_consent_version = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    biometric_enrollment_created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    biometric_enrollment_review_note = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_biometric_enrollment", x => x.biometric_enrollment_id);
                    table.CheckConstraint("ck_enrollment_status", "biometric_enrollment_status IN ('draft','submitted','approved','rejected','revoked')");
                    table.ForeignKey(
                        name: "fk_biometric_enrollment_student_id",
                        column: x => x.student_id,
                        principalTable: "student",
                        principalColumn: "student_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "course_registration",
                columns: table => new
                {
                    course_registration_id = table.Column<Guid>(type: "uuid", nullable: false),
                    student_id = table.Column<Guid>(type: "uuid", nullable: false),
                    academic_term_id = table.Column<Guid>(type: "uuid", nullable: false),
                    course_registration_status = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    course_registration_review_note = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    course_registration_submitted_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    course_registration_reviewed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    course_registration_reviewer_id = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_course_registration", x => x.course_registration_id);
                    table.CheckConstraint("ck_registration_status", "course_registration_status IN ('draft','submitted','corrections','approved','withdrawn')");
                    table.ForeignKey(
                        name: "fk_course_registration_academic_term_id",
                        column: x => x.academic_term_id,
                        principalTable: "academic_term",
                        principalColumn: "academic_term_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_course_registration_course_registration_reviewer_id",
                        column: x => x.course_registration_reviewer_id,
                        principalTable: "account",
                        principalColumn: "account_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_course_registration_student_id",
                        column: x => x.student_id,
                        principalTable: "student",
                        principalColumn: "student_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "academic_schedule",
                columns: table => new
                {
                    academic_schedule_id = table.Column<Guid>(type: "uuid", nullable: false),
                    academic_class_id = table.Column<Guid>(type: "uuid", nullable: false),
                    classroom_id = table.Column<Guid>(type: "uuid", nullable: false),
                    academic_schedule_day_of_week = table.Column<int>(type: "integer", nullable: false),
                    academic_schedule_start = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    academic_schedule_end = table.Column<TimeOnly>(type: "time without time zone", nullable: false),
                    academic_schedule_valid_from = table.Column<DateOnly>(type: "date", nullable: false),
                    academic_schedule_valid_until = table.Column<DateOnly>(type: "date", nullable: false),
                    academic_schedule_time_zone = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_academic_schedule", x => x.academic_schedule_id);
                    table.CheckConstraint("ck_schedule_interval", "academic_schedule_day_of_week BETWEEN 0 AND 6 AND academic_schedule_end > academic_schedule_start AND academic_schedule_valid_until >= academic_schedule_valid_from");
                    table.ForeignKey(
                        name: "fk_academic_schedule_academic_class_id",
                        column: x => x.academic_class_id,
                        principalTable: "academic_class",
                        principalColumn: "academic_class_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_academic_schedule_classroom_id",
                        column: x => x.classroom_id,
                        principalTable: "classroom",
                        principalColumn: "classroom_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "biometric_template",
                columns: table => new
                {
                    biometric_template_id = table.Column<Guid>(type: "uuid", nullable: false),
                    biometric_enrollment_id = table.Column<Guid>(type: "uuid", nullable: false),
                    biometric_template_pose = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    biometric_template_model_sha256 = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    biometric_template_image_sha256 = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    biometric_template_embedding = table.Column<byte[]>(type: "bytea", nullable: false),
                    biometric_template_active = table.Column<bool>(type: "boolean", nullable: false),
                    biometric_template_detection_score = table.Column<double>(type: "double precision", nullable: false),
                    biometric_template_alignment_error = table.Column<double>(type: "double precision", nullable: false),
                    biometric_template_created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_biometric_template", x => x.biometric_template_id);
                    table.CheckConstraint("ck_template_size", "octet_length(biometric_template_embedding) = 2048");
                    table.ForeignKey(
                        name: "fk_biometric_template_biometric_enrollment_id",
                        column: x => x.biometric_enrollment_id,
                        principalTable: "biometric_enrollment",
                        principalColumn: "biometric_enrollment_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "class_membership",
                columns: table => new
                {
                    class_membership_id = table.Column<Guid>(type: "uuid", nullable: false),
                    student_id = table.Column<Guid>(type: "uuid", nullable: false),
                    academic_class_id = table.Column<Guid>(type: "uuid", nullable: false),
                    course_registration_id = table.Column<Guid>(type: "uuid", nullable: false),
                    class_membership_valid_from = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    class_membership_valid_until = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_class_membership", x => x.class_membership_id);
                    table.CheckConstraint("ck_membership_interval", "class_membership_valid_until IS NULL OR class_membership_valid_until >= class_membership_valid_from");
                    table.ForeignKey(
                        name: "fk_class_membership_academic_class_id",
                        column: x => x.academic_class_id,
                        principalTable: "academic_class",
                        principalColumn: "academic_class_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_class_membership_course_registration_id",
                        column: x => x.course_registration_id,
                        principalTable: "course_registration",
                        principalColumn: "course_registration_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_class_membership_student_id",
                        column: x => x.student_id,
                        principalTable: "student",
                        principalColumn: "student_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "course_registration_item",
                columns: table => new
                {
                    course_registration_item_id = table.Column<Guid>(type: "uuid", nullable: false),
                    course_registration_id = table.Column<Guid>(type: "uuid", nullable: false),
                    academic_class_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_course_registration_item", x => x.course_registration_item_id);
                    table.ForeignKey(
                        name: "fk_course_registration_item_academic_class_id",
                        column: x => x.academic_class_id,
                        principalTable: "academic_class",
                        principalColumn: "academic_class_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_course_registration_item_course_registration_id",
                        column: x => x.course_registration_id,
                        principalTable: "course_registration",
                        principalColumn: "course_registration_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "teaching_session",
                columns: table => new
                {
                    teaching_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    academic_class_id = table.Column<Guid>(type: "uuid", nullable: false),
                    lecturer_id = table.Column<Guid>(type: "uuid", nullable: false),
                    classroom_id = table.Column<Guid>(type: "uuid", nullable: false),
                    academic_schedule_id = table.Column<Guid>(type: "uuid", nullable: true),
                    replaces_teaching_session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    teaching_session_start = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    teaching_session_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    teaching_session_early_minutes = table.Column<int>(type: "integer", nullable: false),
                    teaching_session_late_minutes = table.Column<int>(type: "integer", nullable: false),
                    teaching_session_checkin_minutes = table.Column<int>(type: "integer", nullable: false),
                    teaching_session_status = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    teaching_session_roster_frozen = table.Column<bool>(type: "boolean", nullable: false),
                    teaching_session_course_name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    teaching_session_course_credits = table.Column<int>(type: "integer", nullable: false),
                    teaching_session_revision = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_teaching_session", x => x.teaching_session_id);
                    table.CheckConstraint("ck_session_interval", "teaching_session_end > teaching_session_start AND teaching_session_early_minutes BETWEEN 0 AND 180 AND teaching_session_late_minutes BETWEEN 0 AND 180 AND teaching_session_checkin_minutes >= teaching_session_late_minutes");
                    table.CheckConstraint("ck_session_status", "teaching_session_status IN ('scheduled','closed','cancelled')");
                    table.ForeignKey(
                        name: "fk_teaching_session_academic_class_id",
                        column: x => x.academic_class_id,
                        principalTable: "academic_class",
                        principalColumn: "academic_class_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_teaching_session_academic_schedule_id",
                        column: x => x.academic_schedule_id,
                        principalTable: "academic_schedule",
                        principalColumn: "academic_schedule_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_teaching_session_classroom_id",
                        column: x => x.classroom_id,
                        principalTable: "classroom",
                        principalColumn: "classroom_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_teaching_session_lecturer_id",
                        column: x => x.lecturer_id,
                        principalTable: "lecturer",
                        principalColumn: "lecturer_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_teaching_session_replaces_teaching_session_id",
                        column: x => x.replaces_teaching_session_id,
                        principalTable: "teaching_session",
                        principalColumn: "teaching_session_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "attendance_decision",
                columns: table => new
                {
                    attendance_decision_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attendance_event_id = table.Column<Guid>(type: "uuid", nullable: false),
                    student_id = table.Column<Guid>(type: "uuid", nullable: true),
                    teaching_session_id = table.Column<Guid>(type: "uuid", nullable: true),
                    attendance_decision_outcome = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    attendance_decision_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_attendance_decision", x => x.attendance_decision_id);
                    table.ForeignKey(
                        name: "fk_attendance_decision_attendance_event_id",
                        column: x => x.attendance_event_id,
                        principalTable: "attendance_event",
                        principalColumn: "attendance_event_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_attendance_decision_student_id",
                        column: x => x.student_id,
                        principalTable: "student",
                        principalColumn: "student_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_attendance_decision_teaching_session_id",
                        column: x => x.teaching_session_id,
                        principalTable: "teaching_session",
                        principalColumn: "teaching_session_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "session_attendance",
                columns: table => new
                {
                    session_attendance_id = table.Column<Guid>(type: "uuid", nullable: false),
                    teaching_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    student_id = table.Column<Guid>(type: "uuid", nullable: false),
                    attendance_event_id = table.Column<Guid>(type: "uuid", nullable: true),
                    session_attendance_status = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    session_attendance_source = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    session_attendance_occurred_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    session_attendance_late_minutes = table.Column<decimal>(type: "numeric", nullable: false),
                    session_attendance_revision = table.Column<long>(type: "bigint", nullable: false),
                    session_attendance_updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_session_attendance", x => x.session_attendance_id);
                    table.CheckConstraint("ck_attendance_status", "session_attendance_status IN ('present','late','excused','absent') AND session_attendance_source IN ('automatic','manual')");
                    table.ForeignKey(
                        name: "fk_session_attendance_attendance_event_id",
                        column: x => x.attendance_event_id,
                        principalTable: "attendance_event",
                        principalColumn: "attendance_event_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_session_attendance_student_id",
                        column: x => x.student_id,
                        principalTable: "student",
                        principalColumn: "student_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_session_attendance_teaching_session_id",
                        column: x => x.teaching_session_id,
                        principalTable: "teaching_session",
                        principalColumn: "teaching_session_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "session_roster",
                columns: table => new
                {
                    session_roster_id = table.Column<Guid>(type: "uuid", nullable: false),
                    teaching_session_id = table.Column<Guid>(type: "uuid", nullable: false),
                    student_id = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_session_roster", x => x.session_roster_id);
                    table.ForeignKey(
                        name: "fk_session_roster_student_id",
                        column: x => x.student_id,
                        principalTable: "student",
                        principalColumn: "student_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_session_roster_teaching_session_id",
                        column: x => x.teaching_session_id,
                        principalTable: "teaching_session",
                        principalColumn: "teaching_session_id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_academic_class_academic_term_id",
                table: "academic_class",
                column: "academic_term_id");

            migrationBuilder.CreateIndex(
                name: "ix_academic_class_course_id",
                table: "academic_class",
                column: "course_id");

            migrationBuilder.CreateIndex(
                name: "ix_academic_class_lecturer_id",
                table: "academic_class",
                column: "lecturer_id");

            migrationBuilder.CreateIndex(
                name: "ix_academic_schedule_academic_class_id",
                table: "academic_schedule",
                column: "academic_class_id");

            migrationBuilder.CreateIndex(
                name: "ix_academic_schedule_classroom_id",
                table: "academic_schedule",
                column: "classroom_id");

            migrationBuilder.CreateIndex(
                name: "ix_account_account_email",
                table: "account",
                column: "account_email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_account_session_account_id",
                table: "account_session",
                column: "account_id");

            migrationBuilder.CreateIndex(
                name: "ix_account_session_account_session_token_hash",
                table: "account_session",
                column: "account_session_token_hash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_attendance_decision_attendance_event_id",
                table: "attendance_decision",
                column: "attendance_event_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_attendance_decision_student_id",
                table: "attendance_decision",
                column: "student_id");

            migrationBuilder.CreateIndex(
                name: "ix_attendance_decision_teaching_session_id",
                table: "attendance_decision",
                column: "teaching_session_id");

            migrationBuilder.CreateIndex(
                name: "ix_audit_record_audit_occurred_at",
                table: "audit_record",
                column: "audit_occurred_at");

            migrationBuilder.CreateIndex(
                name: "ix_biometric_enrollment_student_id",
                table: "biometric_enrollment",
                column: "student_id",
                unique: true,
                filter: "biometric_enrollment_status = 'draft'");

            migrationBuilder.CreateIndex(
                name: "ix_biometric_template_biometric_enrollment_id_biom_fc10065a3402",
                table: "biometric_template",
                columns: new[] { "biometric_enrollment_id", "biometric_template_image_sha256" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_class_membership_academic_class_id",
                table: "class_membership",
                column: "academic_class_id");

            migrationBuilder.CreateIndex(
                name: "ix_class_membership_course_registration_id",
                table: "class_membership",
                column: "course_registration_id");

            migrationBuilder.CreateIndex(
                name: "ix_class_membership_student_id_academic_class_id",
                table: "class_membership",
                columns: new[] { "student_id", "academic_class_id" },
                unique: true,
                filter: "class_membership_valid_until IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_classroom_classroom_code",
                table: "classroom",
                column: "classroom_code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_course_course_code",
                table: "course",
                column: "course_code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_course_study_program_id",
                table: "course",
                column: "study_program_id");

            migrationBuilder.CreateIndex(
                name: "ix_course_registration_academic_term_id",
                table: "course_registration",
                column: "academic_term_id");

            migrationBuilder.CreateIndex(
                name: "ix_course_registration_course_registration_reviewer_id",
                table: "course_registration",
                column: "course_registration_reviewer_id");

            migrationBuilder.CreateIndex(
                name: "ix_course_registration_student_id_academic_term_id",
                table: "course_registration",
                columns: new[] { "student_id", "academic_term_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_course_registration_item_academic_class_id",
                table: "course_registration_item",
                column: "academic_class_id");

            migrationBuilder.CreateIndex(
                name: "ix_course_registration_item_course_registration_id_6f860edfe16a",
                table: "course_registration_item",
                columns: new[] { "course_registration_id", "academic_class_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_device_room_assignment_classroom_id",
                table: "device_room_assignment",
                column: "classroom_id");

            migrationBuilder.CreateIndex(
                name: "ix_device_room_assignment_device_id",
                table: "device_room_assignment",
                column: "device_id",
                unique: true,
                filter: "device_room_assignment_valid_until IS NULL");

            migrationBuilder.CreateIndex(
                name: "ix_lecturer_account_id",
                table: "lecturer",
                column: "account_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_lecturer_lecturer_number",
                table: "lecturer",
                column: "lecturer_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_session_attendance_attendance_event_id",
                table: "session_attendance",
                column: "attendance_event_id");

            migrationBuilder.CreateIndex(
                name: "ix_session_attendance_student_id",
                table: "session_attendance",
                column: "student_id");

            migrationBuilder.CreateIndex(
                name: "ix_session_attendance_teaching_session_id_student_id",
                table: "session_attendance",
                columns: new[] { "teaching_session_id", "student_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_session_roster_student_id",
                table: "session_roster",
                column: "student_id");

            migrationBuilder.CreateIndex(
                name: "ix_session_roster_teaching_session_id_student_id",
                table: "session_roster",
                columns: new[] { "teaching_session_id", "student_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_student_account_id",
                table: "student",
                column: "account_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_student_advisor_lecturer_id",
                table: "student",
                column: "advisor_lecturer_id");

            migrationBuilder.CreateIndex(
                name: "ix_student_student_identity_id",
                table: "student",
                column: "student_identity_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_student_student_number",
                table: "student",
                column: "student_number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_student_study_program_id",
                table: "student",
                column: "study_program_id");

            migrationBuilder.CreateIndex(
                name: "ix_study_program_study_program_code",
                table: "study_program",
                column: "study_program_code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_teaching_session_academic_class_id_teaching_session_start",
                table: "teaching_session",
                columns: new[] { "academic_class_id", "teaching_session_start" },
                unique: true,
                filter: "teaching_session_status <> 'cancelled'");

            migrationBuilder.CreateIndex(
                name: "ix_teaching_session_academic_schedule_id",
                table: "teaching_session",
                column: "academic_schedule_id");

            migrationBuilder.CreateIndex(
                name: "ix_teaching_session_classroom_id_teaching_session_start",
                table: "teaching_session",
                columns: new[] { "classroom_id", "teaching_session_start" });

            migrationBuilder.CreateIndex(
                name: "ix_teaching_session_lecturer_id",
                table: "teaching_session",
                column: "lecturer_id");

            migrationBuilder.CreateIndex(
                name: "ix_teaching_session_replaces_teaching_session_id",
                table: "teaching_session",
                column: "replaces_teaching_session_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "account_session");

            migrationBuilder.DropTable(
                name: "attendance_decision");

            migrationBuilder.DropTable(
                name: "audit_record");

            migrationBuilder.DropTable(
                name: "biometric_template");

            migrationBuilder.DropTable(
                name: "class_membership");

            migrationBuilder.DropTable(
                name: "course_registration_item");

            migrationBuilder.DropTable(
                name: "device_room_assignment");

            migrationBuilder.DropTable(
                name: "session_attendance");

            migrationBuilder.DropTable(
                name: "session_roster");

            migrationBuilder.DropTable(
                name: "biometric_enrollment");

            migrationBuilder.DropTable(
                name: "course_registration");

            migrationBuilder.DropTable(
                name: "teaching_session");

            migrationBuilder.DropTable(
                name: "student");

            migrationBuilder.DropTable(
                name: "academic_schedule");

            migrationBuilder.DropTable(
                name: "academic_class");

            migrationBuilder.DropTable(
                name: "classroom");

            migrationBuilder.DropTable(
                name: "academic_term");

            migrationBuilder.DropTable(
                name: "course");

            migrationBuilder.DropTable(
                name: "lecturer");

            migrationBuilder.DropTable(
                name: "study_program");

            migrationBuilder.DropTable(
                name: "account");
        }
    }
}
