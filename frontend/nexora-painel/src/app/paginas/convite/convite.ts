import { Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { EquipeServico } from '../../nucleo/servicos/equipe.servico';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { ConviteInfo } from '../../nucleo/modelos';

/** Página PÚBLICA de aceite: o convidado ainda não tem senha nem sessão. Ao aceitar, a API
 *  já devolve o JWT — a pessoa entra direto, sem passar pelo login. */
@Component({
  selector: 'app-convite',
  imports: [FormsModule],
  templateUrl: './convite.html',
  styleUrl: './convite.css'
})
export class Convite implements OnInit {
  private rota = inject(ActivatedRoute);
  private router = inject(Router);
  private servico = inject(EquipeServico);
  private auth = inject(AuthServico);

  private token = '';
  info = signal<ConviteInfo | null>(null);
  carregando = signal(true);
  invalido = signal(false);

  senha = signal('');
  senha2 = signal('');
  salvando = signal(false);
  erro = signal('');

  ngOnInit() {
    this.token = this.rota.snapshot.paramMap.get('token') ?? '';
    this.servico.conviteInfo(this.token).subscribe({
      next: i => { this.info.set(i); this.carregando.set(false); },
      error: () => { this.invalido.set(true); this.carregando.set(false); }
    });
  }

  aceitar() {
    if (this.senha().length < 8) { this.erro.set('A senha precisa de ao menos 8 caracteres.'); return; }
    if (this.senha() !== this.senha2()) { this.erro.set('As senhas não conferem.'); return; }

    this.salvando.set(true);
    this.erro.set('');
    this.servico.aceitarConvite(this.token, this.senha()).subscribe({
      next: r => { this.auth.aplicarLogin(r); this.router.navigate(['/caixa']); },
      error: e => {
        this.erro.set(e.error?.erro ?? 'Convite inválido ou expirado.');
        this.salvando.set(false);
      }
    });
  }
}
