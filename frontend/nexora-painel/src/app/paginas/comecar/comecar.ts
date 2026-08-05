import { Component, OnInit, computed, inject, signal } from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { OnboardingServico } from '../../nucleo/servicos/onboarding.servico';
import { AuthServico } from '../../nucleo/servicos/auth.servico';
import { ToastServico } from '../../nucleo/toast/toast.servico';
import { PassoOnboarding } from '../../nucleo/modelos';

/** PRIMEIROS PASSOS — a tela onde o cliente decide se fica.
 *
 *  ===================== O CHECKLIST É DERIVADO =====================
 *  Nenhum passo tem flag de "já fiz". O servidor responde a três perguntas sobre o estado real
 *  (existe conexão conectada? existe alguém além do dono? chegou alguma mensagem?), e a tela só
 *  desenha a resposta.
 *
 *  Consequência que importa: a empresa que configurou tudo e teve o WhatsApp derrubado duas
 *  semanas depois volta a ver o passo 1 aceso. Com flag, o painel diria "tudo pronto" enquanto
 *  nada chega.
 *  ==================================================================
 *
 *  ===================== E DÁ PARA SAIR =====================
 *  "Pular" existe em dois níveis: o passo da equipe (que muita empresa de uma pessoa só nunca
 *  vai cumprir) e o painel inteiro. Onboarding que prende o usuário irrita mais do que ajuda —
 *  e quem é obrigado a fingir que fez um passo passa a ignorar a tela toda.
 *  ========================================================== */
@Component({
  selector: 'app-comecar',
  imports: [RouterLink],
  templateUrl: './comecar.html',
  styleUrl: './comecar.css'
})
export class Comecar implements OnInit {
  private servico = inject(OnboardingServico);
  private toast = inject(ToastServico);
  private router = inject(Router);
  auth = inject(AuthServico);

  carregando = signal(true);
  erro = signal('');
  ocupado = signal('');

  estado = this.servico.estado;

  passos = computed(() => this.estado()?.passos ?? []);
  concluidos = computed(() => this.estado()?.concluidos ?? 0);
  total = computed(() => this.estado()?.total ?? 3);
  completo = computed(() => this.estado()?.completo ?? false);

  progresso = computed(() => {
    const t = this.total();
    return t === 0 ? 0 : Math.round((this.concluidos() / t) * 100);
  });

  /** O primeiro passo em aberto — é o que a tela destaca. Uma lista de três itens iguais faz o
   *  usuário decidir por onde começar; destacar um só responde a pergunta. */
  proximo = computed(() =>
    this.passos().find(p => !p.concluido && !p.dispensado) ?? null);

  ngOnInit() { this.carregar(); }

  carregar() {
    this.carregando.set(true);
    this.servico.carregar().subscribe({
      next: () => { this.carregando.set(false); this.erro.set(''); },
      error: () => {
        this.erro.set('Não foi possível carregar seus primeiros passos.');
        this.carregando.set(false);
      }
    });
  }

  estado_(p: PassoOnboarding): 'feito' | 'pulado' | 'agora' | 'depois' {
    if (p.concluido) return 'feito';
    if (p.dispensado) return 'pulado';
    return this.proximo()?.chave === p.chave ? 'agora' : 'depois';
  }

  pularEquipe() {
    this.ocupado.set('equipe');
    this.servico.dispensarEquipe().subscribe({
      next: () => { this.ocupado.set(''); this.carregar(); },
      error: e => {
        this.ocupado.set('');
        this.toast.erro(e.error?.erro ?? 'Não foi possível pular este passo.');
      }
    });
  }

  fechar() {
    this.ocupado.set('painel');
    this.servico.dispensar().subscribe({
      next: () => {
        this.ocupado.set('');
        this.servico.estado.set(null);   // some do shell na hora, sem esperar recarga
        this.router.navigate(['/caixa']);
      },
      error: e => {
        this.ocupado.set('');
        this.toast.erro(e.error?.erro ?? 'Não foi possível fechar.');
      }
    });
  }

  irParaCaixa() { this.router.navigate(['/caixa']); }
}
