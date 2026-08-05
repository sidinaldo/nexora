import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { EquipeServico } from '../../nucleo/servicos/equipe.servico';
import { ConviteInfo } from '../../nucleo/modelos';

/** Página PÚBLICA de redefinição: quem perdeu o acesso não tem sessão. Sem envio de e-mail na
 *  fase 1 — o dono gera o link na Equipe e repassa. */
@Component({
  selector: 'app-redefinir',
  imports: [FormsModule],
  templateUrl: './redefinir.html',
  styleUrl: './redefinir.css'
})
export class Redefinir implements OnInit {
  private rota = inject(ActivatedRoute);
  private router = inject(Router);
  private servico = inject(EquipeServico);

  private token = '';
  info = signal<ConviteInfo | null>(null);
  carregando = signal(true);
  invalido = signal(false);
  concluido = signal(false);

  senha = signal('');
  senha2 = signal('');
  salvando = signal(false);
  erro = signal('');

  ngOnInit() {
    this.token = this.rota.snapshot.paramMap.get('token') ?? '';
    this.servico.resetInfo(this.token).subscribe({
      next: i => { this.info.set(i); this.carregando.set(false); },
      error: () => { this.invalido.set(true); this.carregando.set(false); }
    });
  }

  redefinir() {
    if (this.senha().length < 8) { this.erro.set('A senha precisa de ao menos 8 caracteres.'); return; }
    if (this.senha() !== this.senha2()) { this.erro.set('As senhas não conferem.'); return; }

    this.salvando.set(true);
    this.erro.set('');
    this.servico.redefinirSenha(this.token, this.senha()).subscribe({
      next: () => this.concluido.set(true),
      error: e => {
        this.erro.set(e.error?.erro ?? 'Link inválido ou expirado.');
        this.salvando.set(false);
      }
    });
  }

  irParaLogin() { this.router.navigate(['/entrar']); }
}
