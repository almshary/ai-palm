"""Store model reasoning separately from the final answer.

Revision ID: 0003_job_thinking
Revises: 0002_target_host
"""
from typing import Sequence, Union

from alembic import op
import sqlalchemy as sa


revision: str = "0003_job_thinking"
down_revision: Union[str, None] = "0002_target_host"
branch_labels: Union[str, Sequence[str], None] = None
depends_on: Union[str, Sequence[str], None] = None


def upgrade() -> None:
    op.add_column("jobs", sa.Column("thinking", sa.Text(), nullable=False, server_default=""))
    op.alter_column("jobs", "thinking", server_default=None)


def downgrade() -> None:
    op.drop_column("jobs", "thinking")
